namespace Privon.Detection.Detectors;

/// <summary>
/// Canonicalizes a matched bank account string into a separator-free value. No other
/// normalization is applied -- masked positions ('*') are concatenated exactly as captured,
/// never reconstructed into a guessed digit.
/// </summary>
internal static class BankAccountNumberCanonicalizer
{
    public static CanonicalValue Canonicalize(string matchedText) =>
        new(PiiType.BankAccountNumber, StripSeparators(matchedText));

    public static string StripSeparators(string text)
    {
        Span<char> buffer = stackalloc char[text.Length];
        int n = 0;
        foreach (var c in text)
        {
            if (c != '-' && !char.IsWhiteSpace(c)) buffer[n++] = c;
        }
        return new string(buffer[..n]);
    }
}
