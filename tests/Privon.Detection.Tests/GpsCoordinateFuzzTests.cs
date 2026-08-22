using Privon.Detection;
using Privon.Detection.Detectors;

namespace Privon.Detection.Tests;

// Property/fuzz-style tests for GpsCoordinateDetector, mirroring IpAddressFuzzTests.cs /
// MacAddressFuzzTests.cs's approach: no external library added, System.Random with a fixed
// seed instead. All generated strings are structurally-random noise -- never a real,
// identifying coordinate.
public class GpsCoordinateFuzzTests
{
    private static readonly char[] Alphabet =
    [
        '0', '1', '2', '3', '4', '5', '6', '7', '8', '9',
        '.', ',', '-', '+', ' ', '\t', ':', '=',
        'l', 'a', 't', 'o', 'n', 'g', 'i', 'u', 'd', 'e', 'N', 'S', 'E', 'W',
        'G', 'P',
        '위', '도', '경', '좌', '표', '치',
        (char)0x200B, (char)0xFF10, (char)0xFF19,
    ];

    // ---- 26. random input never throws; ---- 27. every span stays inside the raw text ----
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
            var results = new GpsCoordinateDetector().Detect(context);

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
    public void RandomDecimalPairRuns_NeverThrow()
    {
        var numericOnly = new[] { '0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '.', ',', ' ', '-', '+' };
        var random = new Random(20260815);
        for (int iteration = 0; iteration < 500; iteration++)
        {
            int length = random.Next(0, 500);
            var chars = new char[length];
            for (int i = 0; i < length; i++) chars[i] = numericOnly[random.Next(numericOnly.Length)];
            var text = new string(chars);

            var view = NormalizedView.Build(text);
            var results = new GpsCoordinateDetector().Detect(new DetectionContext(view));
            foreach (var c in results)
            {
                Assert.True(c.Span.End <= text.Length);
            }
        }
    }

    // ---- 25. very long input never throws ----
    [Fact]
    public void VeryLongInput_NoException()
    {
        var longText = string.Concat(Enumerable.Repeat("일반 텍스트 문장입니다. ", 20000)) + "lat=37.5665, lon=126.9780";
        var results = new GpsCoordinateDetector().Detect(new DetectionContext(NormalizedView.Build(longText)));
        Assert.Single(results);
    }

    [Fact]
    public void EmptyString_NoException()
    {
        var results = new GpsCoordinateDetector().Detect(new DetectionContext(NormalizedView.Build("")));
        Assert.Empty(results);
    }
}
