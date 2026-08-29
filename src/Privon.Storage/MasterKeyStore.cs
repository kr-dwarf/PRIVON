using System.Security.Cryptography;

namespace Privon.Storage;

/// <summary>
/// Generates and persists the local random master key, protected via Windows DPAPI
/// (CurrentUser scope) so another Windows user account cannot decrypt it.
///
/// Two distinct load paths exist here, deliberately not unified into one behavior:
/// <see cref="LoadOrCreateOrUnavailable"/> is the production fail-safe resolver -- see its own
/// doc for the full three-state (MISSING/AVAILABLE/UNAVAILABLE) contract. <see cref="LoadOrCreate"/>
/// is a narrower legacy-compatibility API -- see its own doc -- that creates a key only when the
/// file is genuinely missing and throws, rather than ever regenerating/overwriting an existing key
/// it cannot use. Neither path silently treats an existing-but-unusable key as absent.
/// </summary>
public static class MasterKeyStore
{
    public const int KeySizeBytes = 32; // AES-256

    public static bool TryLoad(string path, out byte[] key)
    {
        key = [];
        if (!File.Exists(path)) return false;
        try
        {
            var protectedBytes = File.ReadAllBytes(path);
            key = System.Security.Cryptography.ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return key.Length == KeySizeBytes;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public static byte[] CreateAndSave(string path)
    {
        var key = new byte[KeySizeBytes];
        RandomNumberGenerator.Fill(key);
        var protectedBytes = System.Security.Cryptography.ProtectedData.Protect(key, optionalEntropy: null, DataProtectionScope.CurrentUser);
        AtomicFileWriter.WriteAllBytes(path, protectedBytes);
        return key;
    }

    /// <summary>
    /// Legacy compatibility API -- no production caller uses this method as of this hardening (see
    /// <see cref="LoadOrCreateOrUnavailable"/> for the real production resolver); it is kept only
    /// for existing test call sites. Delegates entirely to <see cref="LoadOrCreateOrUnavailable"/>
    /// for the actual MISSING/AVAILABLE/UNAVAILABLE classification: creates and returns a fresh key
    /// only when the file is genuinely missing, and returns an existing valid key unchanged. This
    /// method's own <c>byte[]</c> return type has no way to express "unavailable," so an
    /// existing-but-unusable key (corrupted, wrong length, access-denied, locked) now throws
    /// <see cref="InvalidOperationException"/> instead of the old TryLoad-based behavior, which
    /// silently collapsed that case into "missing" and regenerated/overwrote it -- the fail-closed,
    /// signature-preserving choice for a method whose return type cannot say "no key."
    /// </summary>
    public static byte[] LoadOrCreate(string path)
    {
        var key = LoadOrCreateOrUnavailable(path);
        if (key is null)
        {
            throw new InvalidOperationException(
                "The existing master encryption key is unavailable and could not be used. It was NOT replaced.");
        }

        return key;
    }

    /// <summary>
    /// PRIVON v0.2.1 Gate 3B correction -- the full three-state MASTER_KEY contract, and is the
    /// production resolver that <see cref="LoadOrCreate"/> itself now delegates to entirely (see
    /// that method's own doc). Unlike <see cref="TryLoad"/> (which collapses EVERY non-success
    /// condition -- missing, access-denied, locked, corrupted, wrong length -- into a single
    /// <c>bool</c>, with no way to distinguish "genuinely missing" from "existing but unusable"),
    /// this method distinguishes exactly three states and NEVER calls
    /// <see cref="CreateAndSave"/> for anything except the first:
    ///
    ///   - MASTER_KEY_MISSING: the actual <see cref="File.ReadAllBytes(string)"/> attempt itself
    ///     throws <see cref="FileNotFoundException"/> or <see cref="DirectoryNotFoundException"/>
    ///     -- the ONLY two outcomes treated as "genuinely absent." Creates and returns a fresh
    ///     key. Deliberately does NOT pre-check <see cref="File.Exists(string)"/> first --
    ///     <c>File.Exists</c> is documented to swallow every internal error and return
    ///     <c>false</c> for ANY failure (including one this method must treat as UNAVAILABLE, not
    ///     missing), so using it as a gate would reintroduce exactly the TOCTOU-shaped
    ///     misclassification risk this correction exists to close. The real read attempt's own
    ///     thrown exception type is the only source of truth.
    ///   - MASTER_KEY_AVAILABLE: the file reads, DPAPI-unprotects, AND is exactly
    ///     <see cref="KeySizeBytes"/> long. Returns the existing key, unchanged.
    ///   - MASTER_KEY_UNAVAILABLE (returns <c>null</c>, NEVER calls <see cref="CreateAndSave"/>,
    ///     NEVER touches <paramref name="path"/> in any way): every other outcome for an EXISTING
    ///     file --
    ///       * <see cref="UnauthorizedAccessException"/> (ACL/access-forbidden) or any other
    ///         <see cref="IOException"/> (e.g. currently locked) from the read itself;
    ///       * <see cref="CryptographicException"/> from <see cref="ProtectedData.Unprotect"/>
    ///         (corrupted blob, or protected under a different DPAPI scope/profile);
    ///       * a successfully-unprotected plaintext whose length is not <see cref="KeySizeBytes"/>.
    ///     An existing key that cannot be read, DPAPI-unprotected, or validated must NEVER be
    ///     silently treated as absent and replaced -- in every one of these cases the ORIGINAL
    ///     bytes may still be genuinely recoverable (a fixed ACL, a released lock, or simply
    ///     future forensic/manual recovery), and overwriting them here would be an irreversible,
    ///     destructive action against data that might otherwise still be recoverable. Callers
    ///     (see <see cref="PrivonLocalStore.OpenOrCreate"/>) must treat a <c>null</c> result as "no
    ///     usable key for this store instance" and route every read to that store's own existing
    ///     safe default, and every write to an explicit failure -- never a temporary/replacement
    ///     key, never a silently-reported write success.
    /// </summary>
    public static byte[]? LoadOrCreateOrUnavailable(string path)
    {
        byte[] protectedBytes;
        try
        {
            protectedBytes = File.ReadAllBytes(path);
        }
        catch (FileNotFoundException)
        {
            return CreateAndSave(path); // MASTER_KEY_MISSING -- the file genuinely does not exist
        }
        catch (DirectoryNotFoundException)
        {
            return CreateAndSave(path); // MASTER_KEY_MISSING -- containing directory doesn't exist either
        }
        catch (UnauthorizedAccessException)
        {
            return null; // MASTER_KEY_UNAVAILABLE -- existing key present, access denied
        }
        catch (IOException)
        {
            // MUST be checked after FileNotFoundException/DirectoryNotFoundException above --
            // both are IOException subtypes, and only THEY authorize creation; every other
            // IOException (e.g. sharing violation from a concurrent lock) is UNAVAILABLE.
            return null; // MASTER_KEY_UNAVAILABLE -- existing key present, e.g. currently locked
        }

        byte[] key;
        try
        {
            key = System.Security.Cryptography.ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            return null; // MASTER_KEY_UNAVAILABLE -- DPAPI failure; existing bytes are never touched
        }

        if (key.Length != KeySizeBytes)
            return null; // MASTER_KEY_UNAVAILABLE -- existing key content is invalid; never overwritten

        return key; // MASTER_KEY_AVAILABLE
    }
}
