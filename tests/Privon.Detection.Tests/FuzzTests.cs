using Privon.Detection;
using Privon.Detection.Detectors;

namespace Privon.Detection.Tests;

// Property/fuzz-style tests: no external library added (per the dependency policy) --
// System.Random with a fixed seed gives deterministic, repeatable "random" input instead.
public class FuzzTests
{
    private static readonly char[] Alphabet =
    [
        'a', 'b', 'c', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9',
        '가', '나', '다', '전', '화', '번', '호',
        '-', ' ', '+', '.', ',', '[', ']', '(', ')', '_', '/',
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
            var results = new PhoneDetector().Detect(context);

            foreach (var candidate in results)
            {
                var span = candidate.Span;
                Assert.True(span.Start >= 0, $"negative span start for input length {length}");
                Assert.True(span.Length > 0, $"non-positive span length for input length {length}");
                Assert.True(span.End <= text.Length, $"span exceeds raw text bounds for input length {length}");

                // Must not throw when actually sliced.
                _ = text.Substring(span.Start, span.Length);
            }
        }
    }

    [Fact]
    public void RandomAsciiDigitRuns_NeverThrow()
    {
        var random = new Random(20260814);
        for (int iteration = 0; iteration < 500; iteration++)
        {
            int length = random.Next(0, 500);
            var digits = new char[length];
            for (int i = 0; i < length; i++) digits[i] = (char)('0' + random.Next(10));
            var text = new string(digits);

            var view = NormalizedView.Build(text);
            var results = new PhoneDetector().Detect(new DetectionContext(view));
            foreach (var c in results)
            {
                Assert.True(c.Span.End <= text.Length);
            }
        }
    }
}
