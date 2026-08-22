using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Privon.Core;

namespace Privon.Detection.Detectors;

/// <summary>
/// GPS (WGS-84) decimal-degree coordinate-pair detector.
///
/// Primary sources (no blog/wiki-only basis used):
///   - ISO 6709 ("Standard representation of geographic point location by coordinates"):
///     fixes the field order as latitude-then-longitude, permits a signed decimal-degrees
///     form, and separately defines a hemisphere-letter form where "N"/"S"/"E"/"W" follow
///     immediately after the digits with no space.
///   - RFC 5870 ("A Uniform Resource Identifier for Geographic Locations ('geo' URI)"): fixes
///     latitude range at [-90, 90] and longitude range at [-180, 180], decimal degrees,
///     WGS-84, comma-delimited lat,lon order.
///   - EPSG:4326 / WGS 84 (NGA.STND.0036): the registry's own axis order is latitude then
///     longitude (the common lon,lat convention seen in some software is a de-facto tooling
///     habit, not the registry's own order) -- this is why an unlabeled bare pair below is
///     only ever read as lat-then-lon, never guessed the other way around.
///
/// Only Decimal Degrees (plain signed decimal, and the ISO 6709 hemisphere-letter form) are
/// supported. DMS (degrees/minutes/seconds, e.g. 37°33'59"N) has a real ISO 6709 primary
/// source too, but real-world DMS text varies far more than the officially mandated single
/// string form (straight vs. curly prime/double-prime characters, optional decimal seconds,
/// a Korean "도/분/초" spelled-out variant this product's users would plausibly type) and none
/// of this phase's required test corpus exercises it -- per the "복잡성이 크게 증가하면
/// OPEN_QUESTION/FUTURE_IDEA" instruction, DMS is deliberately deferred rather than forced in;
/// see the Phase 2M report.
///
/// RiskLevel is fixed at Level2 for every candidate this detector reports with strong evidence
/// (an explicit lat/lon label, a GPS/좌표/위치 context keyword, or the hemisphere-letter form).
/// Design doc decision 162 ("GPS 좌표: 위도·경도 형태가 명확하면 정밀 위치 식별정보로 보호한다")
/// has no explicit "Level N" number, but it sits in the same 158~167 "정밀 위치" block as
/// decision 161 (동·호수), which IS explicitly Level2 for the same "구체적 위치를 식별할 수
/// 있는 값" concept, and uses the same "형태가 명확하면 ... 보호한다" shape already mapped to
/// Level2 for IP (163, see IpAddressDetector) and MAC (164, see MacAddressDetector). GPS is
/// not one of the audit contract's enumerated Level3 items either.
///
/// A bare, unlabeled decimal pair (no label, no GPS/location keyword nearby) is deliberately
/// NOT dropped -- design doc decision 57 (지역명만 있는 주소는 Level 1 애매한 정보) already
/// establishes that this codebase's Level1 is meant to carry exactly this kind of
/// structurally-plausible-but-context-weak candidate through to a quiet, non-blocking UI,
/// rather than either hard-rejecting it or force-promoting it to Level2. So a bare pair is
/// reported at RiskLevel.Level1, with DetectionConfidence.Medium when the second value's range
/// alone rules out it being read the other way around, or DetectionConfidence.Low when both
/// values individually fit within [-90, 90] and the lat/lon order is therefore itself
/// ambiguous (see the class's ContextConfidence/OrderConfidence split below).
///
/// This detector does not call into any other detector and is not called into by any other
/// detector (same independence as MacAddressDetector).
/// </summary>
public sealed class GpsCoordinateDetector : IDetector
{
    public string Name => "GpsCoordinateDetector";
    public PiiType PiiType => global::Privon.Detection.PiiType.GpsCoordinate;

    // A decimal point is required in every form this detector supports -- real decimal-degree
    // coordinates always carry a fractional part, and requiring one keeps this detector from
    // matching bare integer pairs (list indices, small counts, etc.) that carry no evidence at
    // all. This is a shape decision, not a value-range decision (range validation happens
    // separately, via double.TryParse + explicit bounds checks below).
    private const string NumberShape = @"[+-]?\d{1,3}\.\d+";

    private static readonly Regex LatKeyPattern = new(
        @"(?<!\p{L})(?:latitude|lat|위도)(?!\p{L})\s*[:=]?\s*(?<val>" + NumberShape + ")",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LonKeyPattern = new(
        @"(?<!\p{L})(?:longitude|lng|lon|경도)(?!\p{L})\s*[:=]?\s*(?<val>" + NumberShape + ")",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LatHemispherePattern = new(
        "(?<val>" + NumberShape + ")(?<hemi>[NSns])", RegexOptions.Compiled);

    private static readonly Regex LonHemispherePattern = new(
        "(?<val>" + NumberShape + ")(?<hemi>[EWew])", RegexOptions.Compiled);

    private static readonly Regex BarePairPattern = new(
        "(?<lat>" + NumberShape + ")(?:,\\s*|[ \\t]+)(?<lon>" + NumberShape + ")",
        RegexOptions.Compiled);

    private static readonly string[] ContextKeywords = ["GPS", "gps", "좌표", "위치"];
    private const int LabelProximityChars = 40;
    private const int ContextWindowChars = 20;

    public IReadOnlyList<DetectionCandidate> Detect(DetectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var results = new List<DetectionCandidate>();
        var claimedSpans = new List<RawSpan>();

        AddLabeledCandidates(context, claimedSpans, results);
        AddHemisphereCandidates(context, claimedSpans, results);
        AddBarePairCandidates(context, claimedSpans, results);

        return results;
    }

    // ---- labeled: lat=/latitude:/위도 paired with lon=/longitude:/경도, either order ----
    private void AddLabeledCandidates(DetectionContext context, List<RawSpan> claimedSpans, List<DetectionCandidate> results)
    {
        var text = context.View.Text;
        var latMatches = LatKeyPattern.Matches(text).Cast<Match>().ToList();
        var lonMatches = LonKeyPattern.Matches(text).Cast<Match>().ToList();
        var lonUsed = new bool[lonMatches.Count];

        foreach (var latM in latMatches)
        {
            int bestIndex = -1, bestDistance = int.MaxValue;
            for (int i = 0; i < lonMatches.Count; i++)
            {
                if (lonUsed[i]) continue;
                int distance = Gap(latM, lonMatches[i]);
                if (distance < bestDistance) { bestDistance = distance; bestIndex = i; }
            }
            if (bestIndex == -1 || bestDistance > LabelProximityChars) continue;
            lonUsed[bestIndex] = true;

            var lonM = lonMatches[bestIndex];
            var latVal = latM.Groups["val"];
            var lonVal = lonM.Groups["val"];

            if (!TryParseInRange(latVal.Value, 90, out _)) continue;
            if (!TryParseInRange(lonVal.Value, 180, out _)) continue;

            int start = Math.Min(latVal.Index, lonVal.Index);
            int end = Math.Max(latVal.Index + latVal.Length, lonVal.Index + lonVal.Length);
            var rawSpan = context.View.IndexMap.ToRawSpan(start, end - start);
            if (OverlapsAny(rawSpan, claimedSpans)) continue;

            var canonical = GpsCoordinateCanonicalizer.Canonicalize(latVal.Value, lonVal.Value);
            AddCandidate(rawSpan, canonical, RiskLevel.Level2, DetectionConfidence.High, claimedSpans, results);
        }
    }

    // ---- hemisphere-letter: "<num>N"/"<num>S" paired with "<num>E"/"<num>W", either order ----
    private void AddHemisphereCandidates(DetectionContext context, List<RawSpan> claimedSpans, List<DetectionCandidate> results)
    {
        var text = context.View.Text;
        var latMatches = LatHemispherePattern.Matches(text).Cast<Match>().ToList();
        var lonMatches = LonHemispherePattern.Matches(text).Cast<Match>().ToList();
        var lonUsed = new bool[lonMatches.Count];

        foreach (var latM in latMatches)
        {
            int bestIndex = -1, bestDistance = int.MaxValue;
            for (int i = 0; i < lonMatches.Count; i++)
            {
                if (lonUsed[i]) continue;
                int distance = Gap(latM, lonMatches[i]);
                if (distance < bestDistance) { bestDistance = distance; bestIndex = i; }
            }
            if (bestIndex == -1 || bestDistance > LabelProximityChars) continue;
            lonUsed[bestIndex] = true;
            var lonM = lonMatches[bestIndex];

            var latValText = latM.Groups["val"].Value;
            var lonValText = lonM.Groups["val"].Value;
            bool latNegative = char.ToUpperInvariant(latM.Groups["hemi"].Value[0]) == 'S';
            bool lonNegative = char.ToUpperInvariant(lonM.Groups["hemi"].Value[0]) == 'W';

            if (!TryParseInRange(latValText, 90, out _)) continue;
            if (!TryParseInRange(lonValText, 180, out _)) continue;

            int start = Math.Min(latM.Index, lonM.Index);
            int endExclusive = Math.Max(latM.Index + latM.Length, lonM.Index + lonM.Length);
            if (!HasCleanOuterBoundary(text, start, endExclusive - start)) continue;

            var rawSpan = context.View.IndexMap.ToRawSpan(start, endExclusive - start);
            if (OverlapsAny(rawSpan, claimedSpans)) continue;

            var signedLat = GpsCoordinateCanonicalizer.ApplyHemisphereSign(latValText, latNegative);
            var signedLon = GpsCoordinateCanonicalizer.ApplyHemisphereSign(lonValText, lonNegative);
            var canonical = GpsCoordinateCanonicalizer.Canonicalize(signedLat, signedLon);
            AddCandidate(rawSpan, canonical, RiskLevel.Level2, DetectionConfidence.High, claimedSpans, results);
        }
    }

    // ---- bare "<lat>, <lon>" or "<lat> <lon>", fixed lat-then-lon order (see class doc) ----
    private void AddBarePairCandidates(DetectionContext context, List<RawSpan> claimedSpans, List<DetectionCandidate> results)
    {
        var text = context.View.Text;
        foreach (Match m in BarePairPattern.Matches(text))
        {
            var latText = m.Groups["lat"].Value;
            var lonText = m.Groups["lon"].Value;

            if (!TryParseInRange(latText, 90, out var latVal)) continue;
            if (!TryParseInRange(lonText, 180, out var lonVal)) continue;

            if (!HasCleanOuterBoundary(text, m.Index, m.Length)) continue;

            var rawSpan = context.View.IndexMap.ToRawSpan(m.Index, m.Length);
            if (OverlapsAny(rawSpan, claimedSpans)) continue;

            var canonical = GpsCoordinateCanonicalizer.Canonicalize(latText, lonText);

            if (HasContextKeyword(text, m.Index, m.Length))
            {
                AddCandidate(rawSpan, canonical, RiskLevel.Level2, DetectionConfidence.High, claimedSpans, results);
                continue;
            }

            // Weak evidence: a bare pair alone never proves GPS intent (design doc decision
            // 57's Level1 precedent -- kept as a quiet, non-blocking candidate, not dropped and
            // not force-promoted). Order ambiguity is folded into Confidence, not RiskLevel:
            // when the longitude value could itself also be read as a latitude, the pair's
            // lat/lon assignment is inherently guessable either way.
            var orderAmbiguous = Math.Abs(lonVal) <= 90;
            var confidence = orderAmbiguous ? DetectionConfidence.Low : DetectionConfidence.Medium;
            AddCandidate(rawSpan, canonical, RiskLevel.Level1, confidence, claimedSpans, results);
        }
    }

    private void AddCandidate(RawSpan rawSpan, CanonicalValue canonical, RiskLevel riskLevel, DetectionConfidence confidence, List<RawSpan> claimedSpans, List<DetectionCandidate> results)
    {
        results.Add(new DetectionCandidate(
            global::Privon.Detection.PiiType.GpsCoordinate,
            rawSpan,
            riskLevel,
            confidence,
            canonical,
            Name));
        claimedSpans.Add(rawSpan);
    }

    private static bool TryParseInRange(string text, double maxAbsolute, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && Math.Abs(value) <= maxAbsolute;

    private static int Gap(Match a, Match b)
    {
        if (a.Index + a.Length <= b.Index) return b.Index - (a.Index + a.Length);
        if (b.Index + b.Length <= a.Index) return a.Index - (b.Index + b.Length);
        return 0; // overlapping -- treat as adjacent, caller still range/boundary-validates
    }

    private static bool HasContextKeyword(string normalizedText, int start, int length)
    {
        int windowStart = Math.Max(0, start - ContextWindowChars);
        int windowEnd = Math.Min(normalizedText.Length, start + length + ContextWindowChars);
        var window = normalizedText[windowStart..windowEnd];

        foreach (var keyword in ContextKeywords)
        {
            if (window.Contains(keyword, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static bool OverlapsAny(RawSpan span, List<RawSpan> others)
    {
        foreach (var other in others)
        {
            if (span.OverlapsWith(other)) return true;
        }
        return false;
    }

    // Boundary hardening, same principle as Ip/MacAddressDetector: a match glued directly to a
    // letter or digit on either side (e.g. "abc37.5665,126.9780xyz") is a fragment of a larger
    // token, not a standalone coordinate, and is rejected outright.
    private static bool HasCleanOuterBoundary(string normalizedText, int start, int length)
    {
        if (start > 0 && char.IsAsciiLetterOrDigit(normalizedText[start - 1])) return false;

        int end = start + length;
        if (end < normalizedText.Length && char.IsAsciiLetterOrDigit(normalizedText[end])) return false;

        return true;
    }
}
