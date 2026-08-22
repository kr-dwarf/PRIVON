using System.Text.RegularExpressions;

namespace Privon.Detection.Detectors;

/// <summary>
/// Canonicalizes a matched card number into a separator-free value: g1+g2+g3+g4. A masked
/// group is already exactly "****" as captured (the only mask character this detector
/// supports) and is concatenated as-is -- never reconstructed into a guessed digit sequence.
/// </summary>
internal static class CardNumberCanonicalizer
{
    public static CanonicalValue Canonicalize(Match match)
    {
        var value = match.Groups["g1"].Value + match.Groups["g2"].Value
            + match.Groups["g3"].Value + match.Groups["g4"].Value;
        return new CanonicalValue(PiiType.CardNumber, value);
    }
}
