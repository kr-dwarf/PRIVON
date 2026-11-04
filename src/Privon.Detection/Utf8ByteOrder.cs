using System.Text;

namespace Privon.Detection;

/// <summary>
/// D3B0-A A.9 §12.2 -- the frozen string comparison for every CCSK field and every ordering the
/// contract calls "UTF-8 byte lexicographic": encode the exact stored code points and compare
/// bytes as unsigned, shorter-is-smaller on a common prefix.
///
/// NOT <c>string.CompareOrdinal</c>: .NET ordinal compares UTF-16 CODE UNITS, which places
/// U+E000-U+FFFF before supplementary-plane characters. This type compares by code point, which
/// is equivalent to comparing RFC 3629 UTF-8 bytes and is platform- and culture-independent.
/// <c>StringComparer.Ordinal</c> and every culture-aware comparer are likewise prohibited here.
/// No Unicode normalization is ever applied.
/// </summary>
internal static class Utf8ByteOrder
{
    /// <summary>
    /// Compares by Unicode scalar value, which is order-equivalent to unsigned UTF-8 byte
    /// lexicographic order. Surrogate pairs are decoded so a supplementary scalar sorts after
    /// every BMP scalar, which is exactly where UTF-8 places it. Inputs are assumed already
    /// validated by <see cref="UnicodeScalarValidator"/> when they are normative fields; an
    /// unpaired surrogate is compared by its own code-unit value rather than throwing, because
    /// ordering must stay total even for a diagnostic path.
    /// </summary>
    public static int Compare(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;

        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            int ca = NextScalar(a, ref i);
            int cb = NextScalar(b, ref j);
            if (ca != cb) return ca < cb ? -1 : 1;
        }

        // Common prefix: shorter is smaller.
        if (i < a.Length) return 1;
        if (j < b.Length) return -1;
        return 0;
    }

    private static int NextScalar(string s, ref int i)
    {
        char c = s[i];
        if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
        {
            int scalar = char.ConvertToUtf32(c, s[i + 1]);
            i += 2;
            return scalar;
        }
        i += 1;
        return c;
    }

    /// <summary>
    /// Canonical RFC 3629 UTF-8 bytes -- shortest form, 1-4 bytes, no over-long forms, no
    /// surrogate code points. NO_REPLACEMENT_FALLBACK (§12.3): the encoder is constructed with
    /// throwing fallbacks, so a replacement-fallback encoder is unreachable from the CCSK path.
    /// </summary>
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static byte[] ToUtf8(string value) => StrictUtf8.GetBytes(value);
}
