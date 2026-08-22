using Privon.Detection;
using Privon.Detection.Detectors;

namespace Privon.Detection.Tests;

// Property/fuzz-style tests for ResidentRegistrationNumberDetector, mirroring FuzzTests.cs's
// approach: no external library added, System.Random with a fixed seed instead. All generated
// strings are structurally-random noise -- never real PII.
public class ResidentRegistrationNumberFuzzTests
{
    private static readonly char[] Alphabet =
    [
        '0', '1', '2', '3', '4', '5', '6', '7', '8', '9',
        '가', '나', '다', '주', '민', '번', '호',
        '-', ' ', '*', 'x', 'X', '.', ',', '[', ']', '_', '/',
        (char)0x200B, (char)0xFF10, (char)0xFF19,
    ];

    [Fact]
    public void RandomStrings_NeverThrow_AndSpansAreAlwaysValid()
    {
        var random = new Random(20260814);

        for (int iteration = 0; iteration < 2000; iteration++)
        {
            int length = random.Next(0, 200);
            var chars = new char[length];
            for (int i = 0; i < length; i++)
            {
                chars[i] = Alphabet[random.Next(Alphabet.Length)];
            }
            var text = new string(chars);

            var view = NormalizedView.Build(text);
            var context = new DetectionContext(view);
            var results = new ResidentRegistrationNumberDetector().Detect(context);

            foreach (var candidate in results)
            {
                var span = candidate.Span;
                Assert.True(span.Start >= 0, $"negative span start for input length {length}");
                Assert.True(span.Length > 0, $"non-positive span length for input length {length}");
                Assert.True(span.End <= text.Length, $"span exceeds raw text bounds for input length {length}");

                _ = text.Substring(span.Start, span.Length);
            }
        }
    }

    [Fact]
    public void RandomDigitRuns_NeverThrow()
    {
        var random = new Random(20260814);
        for (int iteration = 0; iteration < 500; iteration++)
        {
            int length = random.Next(0, 500);
            var digits = new char[length];
            for (int i = 0; i < length; i++) digits[i] = (char)('0' + random.Next(10));
            var text = new string(digits);

            var view = NormalizedView.Build(text);
            var results = new ResidentRegistrationNumberDetector().Detect(new DetectionContext(view));
            foreach (var c in results)
            {
                Assert.True(c.Span.End <= text.Length);
            }
        }
    }
}
