namespace Privon.Detection.Detectors;

/// <summary>
/// Canonicalizes an extracted secret value. Performs NO normalization at all beyond what
/// value-extraction already did (stripping a matched quote pair) -- no case folding, no
/// character removal or trimming, no decoding, no hashing. The caller passes the RAW
/// characters at the resolved span (not the normalized/matched text), so even a zero-width
/// character that was part of the original value survives into the canonical value. Secret
/// values are case-sensitive by definition: two values differing only in case are different
/// secrets and must never canonicalize equal.
/// </summary>
internal static class SecretCanonicalizer
{
    public static CanonicalValue Canonicalize(string extractedValue) =>
        new(PiiType.Secret, extractedValue);
}
