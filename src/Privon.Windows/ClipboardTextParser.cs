using System.Runtime.InteropServices;

namespace Privon.Windows;

/// <summary>
/// Phase 3A.4 STEP3 -- parses a bounded, GlobalSize-limited CF_UNICODETEXT byte buffer into a
/// managed string. Treats the buffer as untrusted input (Phase 3A.4 STEP1 instruction's
/// CLIPBOARD_DATA_IS_UNTRUSTED_INPUT rule): never scans past the caller-supplied bound looking
/// for a NUL terminator (unlike e.g. Marshal.PtrToStringUni, which would read arbitrarily far
/// past the end of a native buffer that turns out not to contain one).
///
/// Pure function, no P/Invoke -- exercised directly by unit tests with synthetic byte arrays,
/// no real clipboard/OS interaction required.
/// </summary>
internal static class ClipboardTextParser
{
    /// <summary>
    /// Returns the text before the first UTF-16 NUL code unit found within <paramref name="buffer"/>,
    /// or null if the buffer is malformed: an odd byte length (not a whole number of UTF-16 code
    /// units) or no NUL terminator anywhere within the bound. Trailing bytes after the NUL --
    /// leftover/uninitialized allocation content -- are never included or otherwise exposed.
    /// </summary>
    public static string? Parse(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length % 2 != 0) return null;

        var chars = MemoryMarshal.Cast<byte, char>(buffer);
        int nulIndex = chars.IndexOf('\0');
        if (nulIndex < 0) return null;

        return new string(chars[..nulIndex]);
    }
}
