using Privon.Core;
using Privon.Detection;
using Privon.Detection.Detectors;

namespace Privon.Detection.Tests;

// Phase 2N.1 — regression-only: locks in design doc decision 165 ("URL 전체를 무조건 제거하지
// 않고 query/path 등에 포함된 이메일·토큰·식별자 등 민감 값만 최소 치환한다") against the
// EXISTING per-PiiType detectors, with no new detector, no new PiiType, and no production code
// changes. See the Phase 2N STEP 1 report: every detector already scans the full normalized
// text regardless of whether a match happens to sit inside a URL, so decision 165 is already
// satisfied without any URL-aware code -- these tests exist only to pin that fact down as a
// regression, not to add new detection behavior.
//
// All values are synthetic: RRN/phone/account/MAC/GPS values are the same synthetic fixtures
// already used elsewhere in this test suite, and the Luhn-valid card number is generated at
// test time from a clearly-non-issuer synthetic prefix (Phase 2N.2 -- no well-known/copied
// real or real-looking issued PAN, per the test data policy: synthetic fixtures are generated,
// not copied from external sources even when the source value isn't itself personal data). No
// real secret, person, or device is referenced anywhere in this file.
public class UrlContextRegressionTests
{
    private static IReadOnlyList<DetectionCandidate> DetectWith(IDetector detector, string rawText)
    {
        var view = NormalizedView.Build(rawText);
        var context = new DetectionContext(view);
        return detector.Detect(context);
    }

    // Synthetic, clearly non-issuer prefix (not a real network BIN pattern) -- same
    // Luhn-completion approach as CardNumberDetectorTests.BuildLuhnValidCard, reimplemented
    // locally since that helper is private to its own test class.
    private const string SyntheticCardPrefix15 = "987654321098765";

    private static string BuildLuhnValidCard(string first15Digits)
    {
        int sum = 0;
        bool doubleDigit = true; // rightmost digit of the 15-digit prefix sits one position
                                  // left of the (unknown) 16th digit, so it is the doubled one.
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

    // ---- 1. a plain, PII-free URL produces no candidate from any detector ----
    [Fact]
    public void PlainUrl_NoCandidateFromAnyDetector()
    {
        const string rawText = "자세한 내용은 https://example.com/products/123 를 참고하세요.";
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

    // ---- 2. email in query: value only, not the whole URL ----
    [Fact]
    public void EmailInQuery_DetectsValueOnly()
    {
        const string email = "synthetic.user@example.com";
        var rawText = $"https://example.com/?email={email}";
        var results = DetectWith(new EmailDetector(), rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(email, rawText.Substring(span.Start, span.Length));
    }

    // ---- 3. email in path: value only ----
    [Fact]
    public void EmailInPath_DetectsValueOnly()
    {
        const string email = "synthetic.user@example.com";
        var rawText = $"https://example.com/users/{email}/profile";
        var results = DetectWith(new EmailDetector(), rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(email, rawText.Substring(span.Start, span.Length));
    }

    // ---- 4. phone in query: value only ----
    [Fact]
    public void PhoneInQuery_DetectsValueOnly()
    {
        const string phone = "01000000000";
        var rawText = $"https://example.com/?phone={phone}";
        var results = DetectWith(new PhoneDetector(), rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(phone, rawText.Substring(span.Start, span.Length));
    }

    // ---- 5. phone in path: value only ----
    [Fact]
    public void PhoneInPath_DetectsValueOnly()
    {
        const string phone = "01000000000";
        var rawText = $"https://example.com/users/{phone}";
        var results = DetectWith(new PhoneDetector(), rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(phone, rawText.Substring(span.Start, span.Length));
    }

    // ---- 6. secret in query: value only, existing evidence threshold untouched ----
    [Fact]
    public void SecretTokenInQuery_DetectsValueOnly()
    {
        const string token = "AbcDef123456789XYZ";
        var rawText = $"https://example.com/?token={token}";
        var results = DetectWith(new SecretDetector(), rawText);

        Assert.Single(results);
        Assert.Equal(RiskLevel.Level3, results[0].RiskLevel);
        var span = results[0].Span;
        Assert.Equal(token, rawText.Substring(span.Start, span.Length));
    }

    // Existing threshold is unchanged: a short/low-diversity query value still isn't detected,
    // same as SecretDetectorTests.DoesNotDetect_ShortUrlQueryToken already locks in.
    [Fact]
    public void SecretTokenInQuery_TooShort_StillNotDetected()
    {
        Assert.Empty(DetectWith(new SecretDetector(), "https://example.com/page?token=abc123xyz"));
    }

    // ---- 7. IP in URL: already covered by IpAddressDetectorTests (DetectsIpv4InsideUrl,
    // Ipv6UrlLiteral_RawSpan_ExcludesBrackets) -- confirmed sufficient, no duplicate test added
    // here per the "don't inflate the corpus" instruction. ----

    // ---- 8. MAC in path/query: URL context is irrelevant to this detector ----
    [Fact]
    public void MacAddressInQuery_DetectsValueOnly()
    {
        const string mac = "02-00-00-00-00-01";
        var rawText = $"https://example.com/?mac={mac}";
        var results = DetectWith(new MacAddressDetector(), rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(mac, rawText.Substring(span.Start, span.Length));
    }

    [Fact]
    public void MacAddressInPath_DetectsValueOnly()
    {
        const string mac = "02:00:00:00:00:01";
        var rawText = $"https://example.com/devices/{mac}";
        var results = DetectWith(new MacAddressDetector(), rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(mac, rawText.Substring(span.Start, span.Length));
    }

    // ---- 9. GPS in URL-like raw text: existing Level1/Level2 policy is unchanged -- being
    // inside a URL is not itself "strong context" and must not promote a bare pair to Level2. ----
    [Fact]
    public void GpsBarePairInQuery_StaysLevel1_NotPromotedByUrlContext()
    {
        var rawText = "https://example.com/?loc=37.5665,126.9780";
        var results = DetectWith(new GpsCoordinateDetector(), rawText);

        Assert.Single(results);
        Assert.Equal(RiskLevel.Level1, results[0].RiskLevel);
        var span = results[0].Span;
        Assert.Equal("37.5665,126.9780", rawText.Substring(span.Start, span.Length));
    }

    [Fact]
    public void GpsLabeledPairInQuery_StaysLevel2_UnchangedPolicy()
    {
        var rawText = "https://example.com/?lat=37.5665&lon=126.9780";
        var results = DetectWith(new GpsCoordinateDetector(), rawText);

        Assert.Single(results);
        Assert.Equal(RiskLevel.Level2, results[0].RiskLevel);
    }

    // ---- 10a. RRN in query: value only ----
    [Fact]
    public void RrnInQuery_DetectsValueOnly()
    {
        const string rrn = "900101-1234568";
        var rawText = $"https://example.com/?rrn={rrn}";
        var results = DetectWith(new ResidentRegistrationNumberDetector(), rawText);

        Assert.Single(results);
        Assert.Equal(RiskLevel.Level3, results[0].RiskLevel);
        var span = results[0].Span;
        Assert.Equal(rrn, rawText.Substring(span.Start, span.Length));
    }

    // ---- 10b. Card in query: a Luhn-valid synthetic test-card number is detected on
    // structure alone, with no English query key acting as -- or standing in for -- context
    // (CardNumberDetector's own context keywords are Korean-only and untouched here). ----
    [Fact]
    public void LuhnValidCardInQuery_DetectsValueOnly()
    {
        // Generated (not copied) synthetic 16-digit Luhn-valid number -- not the
        // "카드번호"-context fixture used elsewhere in this suite, since that fixture relies on
        // a Korean keyword this URL deliberately does not provide.
        var digits16 = BuildLuhnValidCard(SyntheticCardPrefix15);
        var card = $"{digits16[..4]}-{digits16[4..8]}-{digits16[8..12]}-{digits16[12..]}";
        var rawText = $"https://example.com/?card={card}";
        var results = DetectWith(new CardNumberDetector(), rawText);

        Assert.Single(results);
        Assert.Equal(DetectionConfidence.High, results[0].Confidence); // Luhn-valid -> High
        var span = results[0].Span;
        Assert.Equal(card, rawText.Substring(span.Start, span.Length));
    }

    // ---- BankAccount: an English "account=" query key is not a context keyword this
    // detector recognizes (its keywords are Korean: "계좌"/"예금주"/"은행") -- per instructions,
    // this is NOT forced to detect. Confirms current, unmodified behavior; any future
    // "account=" signal is ContextRiskAdjustment's responsibility, not this detector's. ----
    [Fact]
    public void BankAccountInQuery_EnglishKeyAlone_StillNotDetected()
    {
        Assert.Empty(DetectWith(new BankAccountNumberDetector(), "https://example.com/?account=123456789012"));
    }

    // Same digits ARE still detected when the pre-existing Korean context keyword is present,
    // regardless of URL surroundings -- unchanged detector behavior, not a new URL rule.
    [Fact]
    public void BankAccountInQuery_WithExistingKoreanContext_StillDetected()
    {
        var results = DetectWith(new BankAccountNumberDetector(), "https://example.com/?계좌번호=123-456-789012");
        Assert.Single(results);
    }

    // ---- RawSpan discipline: scheme/host/path/query-key characters are never part of any
    // reported span, for any detector exercised above. ----
    [Fact]
    public void RawSpan_NeverIncludesUrlStructuralParts()
    {
        const string email = "synthetic.user@example.com";
        var rawText = $"https://example.com:8443/account/{email}?ref=home#top";
        var results = DetectWith(new EmailDetector(), rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(email, rawText.Substring(span.Start, span.Length));
        Assert.DoesNotContain("https://", rawText.Substring(span.Start, span.Length));
    }

    // ---- Fragment: plain PII inside a URL fragment is still found by full-text scanning --
    // no dedicated fragment parser needed or added. ----
    [Fact]
    public void EmailInFragment_DetectsValueOnly()
    {
        const string email = "synthetic.user@example.com";
        var rawText = $"https://example.com/#email={email}";
        var results = DetectWith(new EmailDetector(), rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(email, rawText.Substring(span.Start, span.Length));
    }

    // ---- Percent-encoding: explicit, locked-in expected limitation for this phase. No decode
    // step exists or is added -- PERCENT_ENCODED_PII_DETECTION stays OPEN_QUESTION. ----
    [Fact]
    public void PercentEncodedEmail_NotDetected_ExpectedLimitation()
    {
        var rawText = "https://example.com/?email=synthetic.user%40example.com";
        Assert.Empty(DetectWith(new EmailDetector(), rawText));
    }
}
