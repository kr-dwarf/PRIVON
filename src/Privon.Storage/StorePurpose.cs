using System.Text;

namespace Privon.Storage;

/// <summary>
/// Distinct per-store identifiers bound into AES-GCM as associated data (AAD). This
/// cryptographically separates stores: ciphertext produced for one purpose fails
/// authentication if decrypted under a different purpose's AAD, even with the correct key.
/// </summary>
public static class StorePurpose
{
    public const string Settings = "PRIVON.Settings.v1";

    // Phase 2R.2: bumped v1 -> v2 for the PiiTypeId schema addition (ExceptionEntry /
    // TrustedPublicInfoEntry). AAD is cryptographically bound into AES-GCM authentication, so
    // a v1-encrypted payload fails authentication outright under the v2 AAD -- it is never
    // silently reinterpreted with a guessed PiiType. This is the intended migration path: old
    // untyped data becomes unreadable, and PrivonLocalStore's existing corruption-fallback
    // path (CryptographicException -> safe empty default) already handles it with no new code.
    public const string TrustedPublicInfo = "PRIVON.TrustedPublicInfo.v2";
    public const string Exceptions = "PRIVON.Exceptions.v2";

    // PRIVON v0.2.1 Gate 3B -- UserExceptionDictionary's own distinct purpose/AAD, deliberately
    // separate from Exceptions/TrustedPublicInfo above: a genuinely different persisted concept
    // (see UserExceptionEntry's own doc), never sharing ciphertext binding with either.
    public const string UserExceptions = "PRIVON.UserExceptions.v1";

    public static byte[] ToAad(string purpose) => Encoding.UTF8.GetBytes(purpose);
}
