using System.Text.RegularExpressions;

namespace Privon.Detection.Detectors;

internal static class PhoneCanonicalizer
{
    public static CanonicalValue Canonicalize(Match match)
    {
        string prefixDigits;
        if (match.Groups["intl"].Success)
        {
            // "+82 10" -> digits "8210" -> strip country code "82", restore domestic leading 0 -> "010"
            var afterPlus = DigitsOnly(match.Groups["intl"].Value);
            prefixDigits = "0" + afterPlus[2..];
        }
        else
        {
            prefixDigits = DigitsOnly(match.Groups["dom"].Value);
        }

        var full = prefixDigits + DigitsOnly(match.Groups["mid"].Value) + DigitsOnly(match.Groups["last"].Value);
        return new CanonicalValue(PiiType.Phone, full);
    }

    private static string DigitsOnly(string s)
    {
        Span<char> buffer = stackalloc char[s.Length];
        int n = 0;
        foreach (var c in s)
        {
            if (char.IsAsciiDigit(c)) buffer[n++] = c;
        }
        return new string(buffer[..n]);
    }
}
