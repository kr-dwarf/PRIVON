namespace Privon.Detection;

/// <summary>
/// D3B0-A A.9 §12.3 ValidUnicodeScalarSequence.
///
/// Over the field's UTF-16 code-unit sequence, left to right: a high surrogate U+D800..U+DBFF is
/// valid ONLY if immediately followed by U+DC00..U+DFFF; a low surrogate is valid ONLY as the
/// second half of such a pair; an unpaired high surrogate (including at end of sequence) is
/// INVALID; an unpaired low surrogate is INVALID. Empty sequence is valid. NO Unicode
/// normalization at any point.
///
/// NO_SILENT_UNICODE_POLICY_EXPANSION: this removes only invalid UTF-16 surrogate
/// representations. Korean, Japanese, emoji, supplementary scalars and combining marks are
/// accepted when scalar-valid. ASCII-only, BMP-only, NFC/NFD, language-specific normalization
/// and case folding are prohibited.
/// </summary>
internal static class UnicodeScalarValidator
{
    public static bool IsValidScalarSequence(string? value)
    {
        if (value is null) return false;

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= value.Length) return false;              // unpaired high at end
                if (!char.IsLowSurrogate(value[i + 1])) return false; // high not followed by low
                i++;                                                  // consume the pair
                continue;
            }
            if (char.IsLowSurrogate(c)) return false;                 // unpaired low
        }
        return true;
    }
}
