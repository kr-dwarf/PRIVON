using Privon.Core;
using Privon.Detection;
using Privon.Detection.Detectors;

namespace Privon.Detection.Tests;

// Phase 2O.2 — regression-only: locks in design doc decision 166 ("입력 텍스트에 파일명이
// 포함돼 있더라도 이름·전화번호·식별번호 등 PII 패턴을 동일하게 탐지한다") against the EXISTING
// per-PiiType detectors (plus the Phase 2O.1 IPv4 trailing-dot hardening), with no new
// detector, no new PiiType, and no further production changes beyond that one hardening fix.
// See the Phase 2O STEP 1 report: filename text needs no dedicated context/pipeline awareness
// for these cases -- the existing detectors already isolate the value correctly.
//
// Two cases are DELIBERATELY left as open, non-pinned ambiguity rather than a locked contract
// (tracked internally as OPEN_QUESTION: FILENAME_EMAIL_EXTENSION_AMBIGUITY /
// FILENAME_SECRET_EXTENSION_AMBIGUITY):
// "user@example.com.txt" (Email) and "token=...XYZ.txt" (Secret) are structurally
// indistinguishable, from raw text alone, between "value + extension" and "value that happens
// to end in those characters" -- see EmailExtensionAmbiguity_OnlyGuaranteesTheRealValueIsFound
// and SecretExtensionAmbiguity_KeepsExistingFailClosedOverProtection below.
//
// All values are synthetic: RRN/phone/MAC/GPS values are the same synthetic fixtures already
// used elsewhere in this test suite, and the Luhn-valid card number is generated at test time
// from a clearly-non-issuer synthetic prefix (same approach as UrlContextRegressionTests /
// CardNumberDetectorTests) -- never a real or well-known copied PAN.
public class FilenameContextRegressionTests
{
    private static IReadOnlyList<DetectionCandidate> DetectWith(IDetector detector, string rawText)
    {
        var view = NormalizedView.Build(rawText);
        var context = new DetectionContext(view);
        return detector.Detect(context);
    }

    // Synthetic, clearly non-issuer prefix -- deterministic Luhn-completion generator, same
    // approach as CardNumberDetectorTests.BuildLuhnValidCard (private to that class, so
    // reimplemented locally rather than reused across test classes).
    private const string SyntheticCardPrefix15 = "135792468013579";

    private static string BuildLuhnValidCard(string first15Digits)
    {
        int sum = 0;
        bool doubleDigit = true;
        for (int i = first15Digits.Length - 1; i >= 0; i--)
        {
            int d = first15Digits[i] - '0';
            if (doubleDigit)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
            doubleDigit = !doubleDigit;
        }
        int checkDigit = (10 - (sum % 10)) % 10;
        return first15Digits + checkDigit;
    }

    // ---- 1. phone in filename: value only ----
    [Fact]
    public void PhoneInFilename_DetectsValueOnly()
    {
        const string phone = "01000000000";
        var rawText = $"customer_{phone}.txt";
        var results = DetectWith(new PhoneDetector(), rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(phone, rawText.Substring(span.Start, span.Length));
    }

    // ---- 2. RRN (masked) in filename: value only ----
    [Fact]
    public void RrnInFilename_DetectsValueOnly()
    {
        const string rrn = "900101-1******";
        var rawText = $"customer_{rrn}.txt";
        var results = DetectWith(new ResidentRegistrationNumberDetector(), rawText);

        Assert.Single(results);
        Assert.Equal(RiskLevel.Level3, results[0].RiskLevel);
        var span = results[0].Span;
        Assert.Equal(rrn, rawText.Substring(span.Start, span.Length));
    }

    // ---- 3. card in filename (Korean context keyword): value only ----
    [Fact]
    public void CardInFilename_WithKoreanContext_DetectsValueOnly()
    {
        var digits16 = BuildLuhnValidCard(SyntheticCardPrefix15);
        var card = $"{digits16[..4]}-{digits16[4..8]}-{digits16[8..12]}-{digits16[12..]}";
        var rawText = $"카드번호_{card}.txt";
        var results = DetectWith(new CardNumberDetector(), rawText);

        Assert.Single(results);
        Assert.Equal(RiskLevel.Level3, results[0].RiskLevel);
        var span = results[0].Span;
        Assert.Equal(card, rawText.Substring(span.Start, span.Length));
    }

    // ---- 4. MAC in filename: value only ----
    [Fact]
    public void MacInFilename_DetectsValueOnly()
    {
        const string mac = "02-00-00-00-00-01";
        var rawText = $"{mac}.txt";
        var results = DetectWith(new MacAddressDetector(), rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(mac, rawText.Substring(span.Start, span.Length));
    }

    // ---- 5. GPS bare pair in filename: unchanged existing policy (Level2/High here because
    // the "gps" context keyword is present, per existing GpsCoordinateDetector policy -- NOT
    // because it's a filename; being a filename never promotes risk on its own). ----
    [Fact]
    public void GpsInFilename_WithGpsKeyword_UnchangedExistingPolicy()
    {
        const string pair = "37.5665,126.9780";
        var rawText = $"gps_{pair}.txt";
        var results = DetectWith(new GpsCoordinateDetector(), rawText);

        Assert.Single(results);
        Assert.Equal(RiskLevel.Level2, results[0].RiskLevel);
        Assert.Equal(DetectionConfidence.High, results[0].Confidence);
        var span = results[0].Span;
        Assert.Equal(pair, rawText.Substring(span.Start, span.Length));
    }

    // ---- 6. GPS labeled pair in filename: span check ----
    [Fact]
    public void GpsLabeledPairInFilename_SpanExcludesExtension()
    {
        const string rawText = "lat=37.5665_lon=126.9780.txt";
        var results = DetectWith(new GpsCoordinateDetector(), rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal("37.5665_lon=126.9780", rawText.Substring(span.Start, span.Length));
        Assert.Equal("37.5665,126.978", results[0].Canonical.Value);
    }

    // ---- 7. phone in a full Windows path: value only ----
    [Fact]
    public void PhoneInWindowsPath_DetectsValueOnly()
    {
        const string phone = "01000000000";
        var rawText = $"C:\\Synthetic\\customer_{phone}.txt";
        var results = DetectWith(new PhoneDetector(), rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(phone, rawText.Substring(span.Start, span.Length));
    }

    // ---- 8. phone in a relative/unix-style path: value only ----
    [Fact]
    public void PhoneInUnixStylePath_DetectsValueOnly()
    {
        const string phone = "01000000000";
        var rawText = $"report/customer_{phone}.txt";
        var results = DetectWith(new PhoneDetector(), rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(phone, rawText.Substring(span.Start, span.Length));
    }

    // ---- 9. email wrapped in parentheses: parens are an unambiguous boundary, value only ----
    [Fact]
    public void EmailInParentheses_DetectsValueOnly()
    {
        const string email = "user@example.com";
        var rawText = $"({email}).txt";
        var results = DetectWith(new EmailDetector(), rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(email, rawText.Substring(span.Start, span.Length));
    }

    // ---- 10. FILENAME_EMAIL_EXTENSION_AMBIGUITY: deliberately NOT pinned as "must exclude
    // .txt". Only asserts the real email is found and never silently dropped -- the exact
    // boundary between "value" and "extension" stays an open question until real filename
    // metadata exists. ----
    [Fact]
    public void EmailExtensionAmbiguity_OnlyGuaranteesTheRealValueIsFound()
    {
        var rawText = "user@example.com.txt";
        var results = DetectWith(new EmailDetector(), rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.StartsWith("user@example.com", rawText.Substring(span.Start, span.Length));
    }

    // ---- 11. FILENAME_SECRET_EXTENSION_AMBIGUITY: Phase 2F.1's fail-closed principle
    // ("secret value 일부를 남기는 위험 < 약간 넓게 보호하는 과보호") is kept exactly as-is --
    // this pins the current, deliberately over-protective behavior as a known tradeoff, not a
    // defect. ----
    [Fact]
    public void SecretExtensionAmbiguity_KeepsExistingFailClosedOverProtection()
    {
        var rawText = "token=AbcDef123456789.txt";
        var results = DetectWith(new SecretDetector(), rawText);

        Assert.Single(results);
        Assert.Equal("AbcDef123456789.txt", results[0].Canonical.Value); // over-protection, by design
    }

    // ---- 12. Phase 2O.1 hardening: IP value only, extension excluded ----
    [Fact]
    public void IpInFilename_LogExtension_DetectsValueOnly()
    {
        const string ip = "192.0.2.1";
        var rawText = $"{ip}.log";
        var results = DetectWith(new IpAddressDetector(), rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(ip, rawText.Substring(span.Start, span.Length));
    }

    // ---- 13. trailing dot+digit still rejected outright, no partial IPv4 cut ----
    [Fact]
    public void IpFollowedByDotDigit_NoPartialMatch()
    {
        Assert.Empty(DetectWith(new IpAddressDetector(), "192.0.2.1.5"));
    }

    // ---- 14. ordinary filenames with no PII produce no candidate from any detector ----
    [Theory]
    [InlineData("report_2026_final.txt")]
    [InlineData("image_001.png")]
    [InlineData("version_1.2.3.txt")]
    public void PlainFilename_NoCandidateFromAnyDetector(string rawText)
    {
        var view = NormalizedView.Build(rawText);
        var context = new DetectionContext(view);

        IDetector[] detectors =
        [
            new PhoneDetector(), new EmailDetector(), new ResidentRegistrationNumberDetector(),
            new CardNumberDetector(), new BankAccountNumberDetector(), new SecretDetector(),
            new IpAddressDetector(), new MacAddressDetector(), new GpsCoordinateDetector(),
        ];

        foreach (var detector in detectors)
        {
            Assert.Empty(detector.Detect(context));
        }
    }
}
