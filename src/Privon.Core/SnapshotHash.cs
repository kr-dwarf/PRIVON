using System.Security.Cryptography;
using System.Text;

namespace Privon.Core;

/// <summary>
/// A one-way hash of the current composer/clipboard text, used only to detect that the
/// text changed. The raw text is never retained -- <see cref="From"/> hashes it and
/// returns immediately; nothing keeps a reference to the input.
///
/// SNAPSHOTHASH_DIAGNOSTIC_SURFACE (Phase 3B STEP15.1): <see cref="ToString"/> is explicitly
/// overridden to exclude the digest entirely. The record-synthesized <c>ToString()</c> this
/// type would otherwise get prints the raw SHA-256 hex digest, which would turn any accidental
/// interpolation/<c>Debug.WriteLine</c>/logger call/exception message into a linkability risk --
/// a short, low-entropy input (e.g. a bare phone number as the entire clipboard text) is
/// effectively brute-forceable from its own hash even though SHA-256 itself is one-way. No
/// prefix/suffix/length-derived fingerprint is included either -- the safest contract is no
/// content-derived metadata at all, matching the identical precedent already established for
/// <c>Privon.Detection.CanonicalValue</c> (Phase 3B STEP3.1) and
/// <c>Privon.Windows.ClipboardTextSnapshot</c> (Phase 3A.4 STEP3.2). This is a diagnostic-surface
/// change only -- <see cref="From"/>'s algorithm, the stored digest, and record equality are all
/// unchanged.
/// </summary>
public readonly record struct SnapshotHash
{
    // Stored as a hex string rather than byte[] so record equality is correct by value
    // (byte[] equality in a record compares by reference, which would be wrong here).
    private readonly string _hex;

    private SnapshotHash(string hex) => _hex = hex;

    public static readonly SnapshotHash Empty = new(string.Empty);

    public static SnapshotHash From(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return Empty;
        var byteCount = Encoding.UTF8.GetByteCount(text);
        Span<byte> utf8 = byteCount <= 512 ? stackalloc byte[byteCount] : new byte[byteCount];
        Encoding.UTF8.GetBytes(text, utf8);
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(utf8, digest);
        utf8.Clear();
        return new SnapshotHash(Convert.ToHexString(digest));
    }

    public override string ToString() => $"{nameof(SnapshotHash)} {{ Redacted }}";
}
