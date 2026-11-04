using Privon.Detection;
using Privon.Detection.Detectors;
using Privon.UsProductIntegration;

namespace Privon.UsProductIntegration.Tests;

// SYNTHETIC DATA ONLY. Differential comparison of the shipped legacy pipeline (DetectionPipeline.CreateDefault(), the
// OverlapResolver path) against the opt-in pipeline (registry + family boundary + CandidateResolver) for Korean and Global
// Core text. A difference is reported exactly; no expectation here is adjusted to fit the implementation.
public class LegacyDifferentialTests
{
    private static readonly string[] Fragments =
    [
        "010-1234-5678", "02-123-4567", "+82 10 1234 5678", "01012345678", "user@example.test", "first.last@mail.example.co.kr",
        "901231-1234567", "9012311234567", "4111 1111 1111 1111", "5500-0000-0000-0004", "4111111111111111", "192.168.10.20", "10.0.0.1",
        "00:1A:2B:3C:4D:5E", "37.5665,126.9780", "37.5665, 126.9780", "api_key=sk_test_abcdefghijklmnop123456",
        "Authorization: Bearer abcdefghijklmnopqrstuvwxyz0123456789", "계좌 110-123-456789", "계좌번호: 1002-123-456789",
        "연락처", "이메일", "샘플", "test", "오늘 날씨가 좋네요", "12345", "2024-05-06", "v1.2.3",
    ];

    private static readonly string[] Joiners = [" ", "\n", ", ", " / ", "\t", " 그리고 ", ": "];

    private static IEnumerable<string> Generate(int seed, int count)
    {
        var rng = new Random(seed);
        for (int i = 0; i < count; i++)
        {
            int parts = rng.Next(1, 6);
            var sb = new System.Text.StringBuilder();
            for (int p = 0; p < parts; p++)
            {
                if (p > 0) sb.Append(Joiners[rng.Next(Joiners.Length)]);
                sb.Append(Fragments[rng.Next(Fragments.Length)]);
            }

            yield return sb.ToString();
        }
    }

    private static IDetector[] LegacyNine() =>
    [
        new PhoneDetector(), new EmailDetector(), new ResidentRegistrationNumberDetector(), new CardNumberDetector(),
        new BankAccountNumberDetector(), new SecretDetector(), new IpAddressDetector(), new MacAddressDetector(),
        new GpsCoordinateDetector(),
    ];

    private static string Key(DetectionCandidate c) =>
        $"{c.PiiType}|{c.Span.Start}:{c.Span.Length}|{c.Canonical?.Value}|{c.RiskLevel}|{c.Confidence}";

    [Fact]
    public void ForKoreanAndGlobalText_TheOptInPipelineReturnsTheSameCandidatesAsTheShippedPipeline()
    {
        var shipped = DetectionPipeline.CreateDefault();
        var optIn = UsOptInPipeline.Create(LegacyNine());
        var differences = new List<string>();
        int total = 0;
        int withCandidates = 0;

        foreach (string raw in Generate(seed: 20260901, count: 4000))
        {
            total++;
            var expected = shipped.Detect(raw).Candidates.Select(Key).OrderBy(x => x, StringComparer.Ordinal).ToList();
            List<string> actual;
            try
            {
                actual = optIn.Detect(raw).Candidates.Select(Key).OrderBy(x => x, StringComparer.Ordinal).ToList();
            }
            catch (Exception ex)
            {
                differences.Add($"{raw.Replace("\n", "\\n").Replace("\t", "\\t")} => THROWS {ex.GetType().Name}");
                continue;
            }

            if (expected.Count > 0) withCandidates++;
            if (!expected.SequenceEqual(actual))
            {
                differences.Add($"{raw.Replace("\n", "\\n").Replace("\t", "\\t")} => shipped[{string.Join(" ; ", expected)}] optIn[{string.Join(" ; ", actual)}]");
            }
        }

        Assert.True(withCandidates > 500, $"the corpus must be meaningful: {withCandidates} of {total} inputs had a candidate");
        Assert.True(differences.Count == 0, $"{differences.Count} of {total} differ: " + string.Join(" // ", differences.Take(6)));
    }
}
