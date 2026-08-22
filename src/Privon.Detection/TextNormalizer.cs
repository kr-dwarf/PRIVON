using System.Text;

namespace Privon.Detection;

/// <summary>
/// Builds the detection-only NormalizedView from RawText. RawText itself is never modified
/// -- this only ever produces a *separate* normalized string plus an index map back to the
/// original positions. Scope is intentionally minimal for Phase 2A: fullwidth-digit folding
/// and zero-width character removal (the two behaviors detectors currently need). Broader
/// normalization (spacing collapse, etc.) is left to individual detector patterns for now.
/// </summary>
internal static class TextNormalizer
{
    public static (string Text, RawIndexMap Map) Normalize(string rawText)
    {
        var sb = new StringBuilder(rawText.Length);
        var map = new int[rawText.Length];
        int normalizedLength = 0;

        for (int i = 0; i < rawText.Length; i++)
        {
            char c = rawText[i];
            if (IsZeroWidth(c)) continue;

            sb.Append(FoldFullwidthDigit(c));
            map[normalizedLength] = i;
            normalizedLength++;
        }

        if (normalizedLength != map.Length)
        {
            Array.Resize(ref map, normalizedLength);
        }

        return (sb.ToString(), new RawIndexMap(map));
    }

    // Expressed as numeric code-point casts rather than character literals so no
    // invisible/ambiguous Unicode characters are embedded in the source file itself.
    private const char ZeroWidthSpace = (char)0x200B;
    private const char ZeroWidthNonJoiner = (char)0x200C;
    private const char ZeroWidthJoiner = (char)0x200D;
    private const char ZeroWidthNoBreakSpace = (char)0xFEFF; // BOM when it appears mid-text
    private const char FullwidthDigitZero = (char)0xFF10;
    private const char FullwidthDigitNine = (char)0xFF19;

    private static bool IsZeroWidth(char c) =>
        c == ZeroWidthSpace || c == ZeroWidthNonJoiner || c == ZeroWidthJoiner || c == ZeroWidthNoBreakSpace;

    private static char FoldFullwidthDigit(char c) =>
        c >= FullwidthDigitZero && c <= FullwidthDigitNine ? (char)(c - FullwidthDigitZero + '0') : c;
}
