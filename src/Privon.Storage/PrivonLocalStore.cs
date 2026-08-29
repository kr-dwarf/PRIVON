using System.Security.Cryptography;
using System.Text.Json;

namespace Privon.Storage;

/// <summary>
/// Top-level API for Phase 1 local storage. Every Load* method is total -- it never throws
/// for corruption, tampering, or a missing file; it returns the safe default for that
/// store instead. Detection Engine (Phase 2) is not implemented yet; this class only
/// provides the storage structure and API described in the audit contract.
/// </summary>
public sealed class PrivonLocalStore
{
    private readonly string _settingsPath;
    private readonly string _trustedPath;
    private readonly string _exceptionsPath;
    private readonly string _userExceptionsPath;

    // PRIVON v0.2.1 Gate 3B correction -- MASTER_KEY_UNAVAILABLE representation: the single
    // Storage-owned source of truth for "is a usable root key present for THIS instance." Null
    // means the underlying master.key exists but could not be read (ACL/access-forbidden, or
    // currently locked -- see MasterKeyStore.LoadOrCreateOrUnavailable's own doc) -- never that
    // no key exists at all (that case always creates and returns a real key, exactly as before).
    // Every Load* method below treats null the same way it already treats any other
    // unrecoverable-read condition (returns that call's own existing safeDefault); every Save
    // method throws immediately rather than ever encrypting under a temporary/replacement key.
    // Deliberately NOT re-resolved later -- a fresh PrivonLocalStore.OpenOrCreate call (e.g. after
    // the ACL is fixed) is what re-attempts loading the ORIGINAL key; this instance never retries.
    private readonly byte[]? _masterKey;

    private PrivonLocalStore(string rootDirectory, byte[]? masterKey)
    {
        _settingsPath = Path.Combine(rootDirectory, "settings.bin");
        _trustedPath = Path.Combine(rootDirectory, "trusted-public-info.bin");
        _exceptionsPath = Path.Combine(rootDirectory, "exceptions.bin");
        _userExceptionsPath = Path.Combine(rootDirectory, "user-exceptions.bin");
        _masterKey = masterKey;
    }

    // PRIVON v0.2.1 Gate 3C -- the single Storage-owned, metadata-only signal an honest Settings UI
    // needs to represent MASTER_KEY_UNAVAILABLE state (see the _masterKey field's own doc above).
    // Exposes state only -- no key/crypto material of any kind -- and is not a second source of
    // truth: it is derived directly from the exact same _masterKey this instance already resolved
    // once, at construction, in OpenOrCreate. Never re-checked/re-resolved later (a fresh
    // OpenOrCreate call is what re-attempts loading the original key -- this instance never retries;
    // see MasterKeyStore.LoadOrCreateOrUnavailable's own doc).
    public bool IsMasterKeyUnavailable => _masterKey is null;

    // NEVER throws for an unreadable-but-present master.key (see MASTER_KEY_UNAVAILABLE doc
    // above) -- this instance is still fully constructed and usable in a protective, read-safe-
    // defaults/write-fails mode, so callers such as PrivonAppComposition never need their own
    // fallback path for this condition.
    public static PrivonLocalStore OpenOrCreate(string rootDirectory)
    {
        Directory.CreateDirectory(rootDirectory);
        var keyPath = Path.Combine(rootDirectory, "master.key");
        var masterKey = MasterKeyStore.LoadOrCreateOrUnavailable(keyPath);
        return new PrivonLocalStore(rootDirectory, masterKey);
    }

    // BACKWARD_COMPATIBILITY (PRIVON v0.2.1 Gate 3A): settings persisted before
    // ProtectionCategorySettings existed deserialize with Categories == null (the property is
    // simply absent from the old JSON) -- never trusted as "user chose no categories," always
    // resolved to AllOn here, the same fail-safe-never-weakens-protection guarantee corruption
    // itself already gets via LoadOrDefault's own safeDefault fallback below.
    public PrivonSettings LoadSettings() =>
        LoadOrDefault(_settingsPath, StorePurpose.Settings, PrivonSettings.SafeDefault, b =>
        {
            var settings = JsonSerializer.Deserialize<PrivonSettings>(b) ?? PrivonSettings.SafeDefault;
            return settings.Categories is null ? settings with { Categories = ProtectionCategorySettings.AllOn } : settings;
        });

    public void SaveSettings(PrivonSettings settings) =>
        Save(_settingsPath, StorePurpose.Settings, JsonSerializer.SerializeToUtf8Bytes(settings));

    public IReadOnlyList<TrustedPublicInfoEntry> LoadTrustedPublicInfo() =>
        LoadOrDefault(_trustedPath, StorePurpose.TrustedPublicInfo, (IReadOnlyList<TrustedPublicInfoEntry>)[],
            b => ValidateStructure(JsonSerializer.Deserialize<List<TrustedPublicInfoEntry>>(b) ?? [],
                e => (e.PiiTypeId, e.Value)));

    public void SaveTrustedPublicInfo(IReadOnlyList<TrustedPublicInfoEntry> entries)
    {
        RequireValidStructure(entries, e => (e.PiiTypeId, e.Value));
        Save(_trustedPath, StorePurpose.TrustedPublicInfo, JsonSerializer.SerializeToUtf8Bytes(entries));
    }

    public IReadOnlyList<ExceptionEntry> LoadExceptions() =>
        LoadOrDefault(_exceptionsPath, StorePurpose.Exceptions, (IReadOnlyList<ExceptionEntry>)[],
            b => ValidateStructure(JsonSerializer.Deserialize<List<ExceptionEntry>>(b) ?? [],
                e => (e.PiiTypeId, e.Value)));

    public void SaveExceptions(IReadOnlyList<ExceptionEntry> entries)
    {
        RequireValidStructure(entries, e => (e.PiiTypeId, e.Value));
        Save(_exceptionsPath, StorePurpose.Exceptions, JsonSerializer.SerializeToUtf8Bytes(entries));
    }

    // PRIVON v0.2.1 Gate 3B -- UserExceptionDictionary's own genuinely separate persisted
    // structure: own file (user-exceptions.bin), own StorePurpose (distinct AAD binding, so
    // ciphertext produced for this store fails authentication under Settings/Exceptions/
    // TrustedPublicInfo's own purpose and vice versa), same LoadOrDefault fail-safe fallback
    // (missing/corrupt/unreadable -> empty list, inheriting the UnauthorizedAccessException fix
    // from Gate 3A's correction turn for free) and the same ValidateStructure/RequireValidStructure
    // discipline as ExceptionEntry/TrustedPublicInfoEntry.
    public IReadOnlyList<UserExceptionEntry> LoadUserExceptions() =>
        LoadOrDefault(_userExceptionsPath, StorePurpose.UserExceptions, (IReadOnlyList<UserExceptionEntry>)[],
            b => ValidateStructure(JsonSerializer.Deserialize<List<UserExceptionEntry>>(b) ?? [],
                e => (e.PiiTypeId, e.Value)));

    public void SaveUserExceptions(IReadOnlyList<UserExceptionEntry> entries)
    {
        RequireValidStructure(entries, e => (e.PiiTypeId, e.Value));
        Save(_userExceptionsPath, StorePurpose.UserExceptions, JsonSerializer.SerializeToUtf8Bytes(entries));
    }

    // Phase 2R.2: a structurally malformed entry (missing/null/empty/whitespace PiiTypeId or
    // Value -- never a PII-format check, Storage does not validate Phone/Email shapes) makes
    // the WHOLE store untrustworthy on load, not just that one entry. Throwing JsonException
    // here routes through LoadOrDefault's existing corruption-fallback path (safe empty
    // default) with no new fallback logic -- deliberately not filtering out just the bad
    // entry, since silently keeping "the rest" could mask a real write-time bug and give a
    // false impression that the surviving entries are trustworthy.
    private static IReadOnlyList<T> ValidateStructure<T>(List<T> entries, Func<T, (string PiiTypeId, string Value)> select)
    {
        foreach (var entry in entries)
        {
            var (piiTypeId, value) = select(entry);
            if (string.IsNullOrWhiteSpace(piiTypeId) || string.IsNullOrWhiteSpace(value))
            {
                throw new JsonException("Malformed persisted entry: PiiTypeId and Value must be non-empty.");
            }
        }
        return entries;
    }

    // Save-side mirror of the same structural check -- a caller must never be able to persist
    // an entry Load would later refuse to trust.
    private static void RequireValidStructure<T>(IReadOnlyList<T> entries, Func<T, (string PiiTypeId, string Value)> select)
    {
        foreach (var entry in entries)
        {
            var (piiTypeId, value) = select(entry);
            if (string.IsNullOrWhiteSpace(piiTypeId) || string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Malformed entry: PiiTypeId and Value must be non-empty.", nameof(entries));
            }
        }
    }

    private T LoadOrDefault<T>(string path, string purpose, T safeDefault, Func<byte[], T> deserialize)
    {
        // MASTER_KEY_UNAVAILABLE (PRIVON v0.2.1 Gate 3B correction): no usable root key for this
        // instance -- nothing under it could ever be decrypted, so this resolves exactly like any
        // other unrecoverable-read condition, via the SAME safeDefault every other branch below
        // already returns. No new per-store fallback value, no App-layer duplication.
        if (_masterKey is null) return safeDefault;
        if (!File.Exists(path)) return safeDefault;
        try
        {
            var raw = File.ReadAllBytes(path);
            var envelope = SecureEnvelope.Decode(raw);
            var plaintext = SecureEnvelopeCodec.Decrypt(_masterKey, purpose, envelope);
            return deserialize(plaintext);
        }
        catch (CryptographicException)
        {
            return safeDefault;
        }
        catch (EnvelopeFormatException)
        {
            return safeDefault;
        }
        catch (JsonException)
        {
            return safeDefault;
        }
        catch (IOException)
        {
            return safeDefault;
        }
        catch (UnauthorizedAccessException)
        {
            // PRIVON v0.2.1 Gate 3A unreadable-settings correction: File.ReadAllBytes throws
            // UnauthorizedAccessException (not IOException) when the file exists but access is
            // denied (ACL deny rule, equivalent access-forbidden condition). This is a READ
            // fallback only -- see Save's own doc; a failed write is never routed through this
            // method and must keep throwing. Safe for every current LoadOrDefault caller
            // (LoadSettings/LoadTrustedPublicInfo/LoadExceptions): an unreadable file is
            // indistinguishable, from this method's own vantage point, from a corrupted one --
            // both are "the real persisted data cannot be recovered right now" -- and every
            // caller's existing safeDefault already resolves that exact condition safely
            // (LoadSettings -> Categories.AllOn/ProtectionEnabled=true; LoadTrustedPublicInfo/
            // LoadExceptions -> empty list, which can only ever make protection MORE strict, per
            // TrustExceptionProvider's own STORAGE_FAILURE_POLICY doc -- losing trust/exception
            // data never grants trust or creates an exception it would not otherwise have).
            return safeDefault;
        }
    }

    // MASTER_KEY_UNAVAILABLE write policy (PRIVON v0.2.1 Gate 3B correction): a write must NEVER
    // silently report success, and must NEVER encrypt under a temporary/replacement key -- so
    // this throws immediately, before any encryption or file I/O is even attempted, whenever this
    // instance has no usable root key. The thrown type is deliberately generic
    // (InvalidOperationException) -- this is a caller/programmer-visible precondition failure
    // ("you are trying to persist while the store is in its protective, key-unavailable mode"),
    // not a Win32/crypto failure mode any existing catch clause elsewhere is meant to absorb.
    private void Save(string path, string purpose, byte[] plaintext)
    {
        if (_masterKey is null)
        {
            throw new InvalidOperationException(
                "Cannot save: the master encryption key is currently unavailable (an existing " +
                "master.key is present but could not be read). Writing under a temporary or " +
                "replacement key is never permitted.");
        }

        var envelope = SecureEnvelopeCodec.Encrypt(_masterKey, purpose, plaintext);
        AtomicFileWriter.WriteAllBytes(path, envelope.Encode());
    }
}
