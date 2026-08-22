using System.Text.RegularExpressions;

namespace Privon.Detection.Detectors;

/// <summary>
/// Canonicalizes a matched email into a comparable value. Only case-folds the address --
/// local-part dots, plus-tags, and hyphens are preserved exactly as matched, so two
/// genuinely different addresses (e.g. "test.user@" vs "testuser@") never collapse into the
/// same canonical value. The one representational normalization applied is turning an
/// obfuscated domain separator ("(dot)") back into a literal "." so the same real address
/// written in obfuscated vs. plain form still canonicalizes identically.
/// </summary>
internal static class EmailCanonicalizer
{
    public static CanonicalValue Canonicalize(Match match)
    {
        var local = match.Groups["local"].Value;
        var domain = DotMarkerPattern.Replace(match.Groups["domain"].Value, ".");
        return new CanonicalValue(PiiType.Email, (local + "@" + domain).ToLowerInvariant());
    }

    private static readonly Regex DotMarkerPattern = new(
        @"\s*(?:\.|\(\s*dot\s*\))\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
}
