using System.Text.RegularExpressions;

namespace Privon.Detection.Detectors;

/// <summary>
/// Canonicalizes a matched resident registration number into a comparable, separator-free
/// value: yy+mm+dd+gender+back. The back segment is preserved as-is when it is six real
/// digits; when it is masked, the mask characters ('*'/'x'/'X') are normalized to a fixed
/// "******" run so the same masked number written with different mask characters still
/// compares equal -- this does not invent or reveal any digit that was not visible.
/// </summary>
internal static class ResidentRegistrationNumberCanonicalizer
{
    public static CanonicalValue Canonicalize(Match match)
    {
        var back = match.Groups["back"].Value;
        var normalizedBack = IsAllDigits(back) ? back : new string('*', back.Length);
        var value = match.Groups["yy"].Value + match.Groups["mm"].Value + match.Groups["dd"].Value
            + match.Groups["gender"].Value + normalizedBack;
        return new CanonicalValue(PiiType.ResidentRegistrationNumber, value);
    }

    private static bool IsAllDigits(string s)
    {
        foreach (var c in s)
        {
            if (!char.IsAsciiDigit(c)) return false;
        }
        return true;
    }
}
