namespace Privon.Detection.Detectors;

/// <summary>
/// Canonicalizes a latitude/longitude pair to "&lt;lat&gt;,&lt;lon&gt;" -- always in that
/// fixed order regardless of which order the raw text used (a labeled "lon=... lat=..." and
/// "lat=... lon=..." converge on the same CanonicalValue), matching the lat-before-lon field
/// order fixed by ISO 6709 and RFC 5870.
///
/// Normalization is string-level, never a float round-trip: a redundant leading '+' and
/// trailing fractional zeros are stripped (these never change the represented value), but no
/// digit is ever rounded or dropped -- "37.5665" and "37.5665001" stay distinct values. See
/// GpsCoordinateDetector's class doc for the "좌표 반올림 금지" source requirement.
/// </summary>
internal static class GpsCoordinateCanonicalizer
{
    public static CanonicalValue Canonicalize(string latRaw, string lonRaw) =>
        new(PiiType.GpsCoordinate, $"{NormalizeNumber(latRaw)},{NormalizeNumber(lonRaw)}");

    /// <summary>
    /// Combines a hemisphere-letter-form magnitude (e.g. the "37.5665" in "37.5665N") with the
    /// sign the hemisphere letter carries (N/E positive, S/W negative) into a signed decimal
    /// string, ready for <see cref="Canonicalize"/>. Allowed per the class doc's "N/S/E/W 표현을
    /// signed decimal로 변환하는 것은 공식 의미가 명확하면 허용" source note.
    /// </summary>
    public static string ApplyHemisphereSign(string rawMagnitude, bool negative)
    {
        var unsigned = rawMagnitude.Length > 0 && (rawMagnitude[0] == '+' || rawMagnitude[0] == '-')
            ? rawMagnitude[1..]
            : rawMagnitude;
        return negative ? "-" + unsigned : unsigned;
    }

    private static string NormalizeNumber(string raw)
    {
        bool negative = raw.Length > 0 && raw[0] == '-';
        var s = raw.Length > 0 && (raw[0] == '+' || raw[0] == '-') ? raw[1..] : raw;

        int dot = s.IndexOf('.');
        if (dot >= 0)
        {
            int end = s.Length;
            while (end > dot + 1 && s[end - 1] == '0') end--;
            if (end == dot + 1) end = dot; // fractional part was all zeros -- drop the dot too
            s = s[..end];
        }

        return negative ? "-" + s : s;
    }
}
