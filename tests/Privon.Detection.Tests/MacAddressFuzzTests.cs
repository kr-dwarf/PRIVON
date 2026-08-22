using Privon.Detection;
using Privon.Detection.Detectors;

namespace Privon.Detection.Tests;

// Property/fuzz-style tests for MacAddressDetector, mirroring IpAddressFuzzTests.cs's
// approach: no external library added, System.Random with a fixed seed instead. All generated
// strings are structurally-random noise -- never a real MAC address.
public class MacAddressFuzzTests
{
    private static readonly char[] Alphabet =
    [
        '0', '1', '2', '3', '4', '5', '6', '7', '8', '9',
        'a', 'b', 'c', 'd', 'e', 'f', 'A', 'B', 'C', 'D', 'E', 'F',
        '가', '나', '다',
        '-', ' ', '.', ',', ':', '[', ']', 'M', 'A', 'C',
        (char)0x200B, (char)0xFF10, (char)0xFF19,
    ];

    // ---- 18/19. very long / random input never throws; every span stays valid ----
    [Fact]
    public void RandomStrings_NeverThrow_AndSpansAreAlwaysValid()
    {
        var random = new Random(20260815);

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
            var results = new MacAddressDetector().Detect(context);

            foreach (var candidate in results)
            {
                var span = candidate.Span;
                Assert.True(span.Start >= 0, $"negative span start for input length {length}");
                Assert.True(span.Length > 0, $"non-positive span length for input length {length}");
                Assert.True(span.End <= text.Length, $"span exceeds raw text bounds for input length {length}");

                // ---- 20. RawSpan always inside the original raw text ----
                _ = text.Substring(span.Start, span.Length);
            }
        }
    }

    [Fact]
    public void RandomHexColonAndHyphenRuns_NeverThrow()
    {
        var hexAndSeparators = new[] { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', 'a', 'b', 'c', 'd', 'e', 'f', ':', '-' };
        var random = new Random(20260815);
        for (int iteration = 0; iteration < 500; iteration++)
        {
            int length = random.Next(0, 500);
            var chars = new char[length];
            for (int i = 0; i < length; i++) chars[i] = hexAndSeparators[random.Next(hexAndSeparators.Length)];
            var text = new string(chars);

            var view = NormalizedView.Build(text);
            var results = new MacAddressDetector().Detect(new DetectionContext(view));
            foreach (var c in results)
            {
                Assert.True(c.Span.End <= text.Length);
            }
        }
    }

    // ---- 18. very long input never throws ----
    [Fact]
    public void VeryLongInput_NoException()
    {
        var longText = string.Concat(Enumerable.Repeat("일반 텍스트 문장입니다. ", 20000)) + "02:00:00:00:00:01";
        var results = new MacAddressDetector().Detect(new DetectionContext(NormalizedView.Build(longText)));
        Assert.Single(results);
    }

    [Fact]
    public void EmptyString_NoException()
    {
        var results = new MacAddressDetector().Detect(new DetectionContext(NormalizedView.Build("")));
        Assert.Empty(results);
    }
}
