using System.Collections.Concurrent;
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
    private readonly string _countryChoicePath;
    private readonly string _countryActivationPath;

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
        _countryChoicePath = Path.Combine(rootDirectory, "country-settings.bin");
        _countryActivationPath = Path.Combine(rootDirectory, "country-activation.bin");
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

    // ================================================================================================================
    // PRIVON 0.4.0 T-1 -- the per-country ON/OFF choice (see CountryChoice.cs for what a readable record does and does not prove).
    //
    // Its own file (country-settings.bin) and purpose, the same master key / AES-GCM envelope / atomic replace as every other store here. PrivonSettings
    // and settings.bin are untouched, so an older settings file keeps loading exactly as before and neither file can clobber the other.
    //
    //   * A record reads as On / Off only when: a usable key exists, the authentication tag verifies (this Windows user's key + this purpose), the
    //     document is valid, its format version is known, its origin is known, and its revision is not older than one this process has already seen.
    //   * Anything else is a NON-choice (Missing / Corrupt / Unauthenticated / UnknownVersion) and the caller keeps the country pack active.
    //   * Writes are serialized per file, increase the revision, replace the file atomically (the old record survives any failed or interrupted
    //     write), refuse to overwrite a record of a newer version, and never write without a usable key.
    //
    // LIMITS: the revision guard catches a rollback only WITHIN this process; a file restored while PRIVON is not running reads as the older (valid)
    // record, because nothing outside the user's own files can say otherwise. And code running as the same Windows user can read the key and write a
    // valid record: this store gives integrity and per-user scope, not a defense against that.
    // ================================================================================================================

    private static readonly ConcurrentDictionary<string, object> CountryLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, long> CountryHighestRevision = new(StringComparer.OrdinalIgnoreCase);

    private enum CountryFileKind
    {
        Ok,
        Missing,
        Corrupt,
        AuthFailed,
        IoFailed,
        UnknownVersion,
    }

    private (CountryFileKind Kind, CountryChoiceDocument.Doc? Doc) ReadCountryFile(string path, string purpose)
    {
        if (_masterKey is null) return (CountryFileKind.AuthFailed, null);
        if (!File.Exists(path)) return (CountryFileKind.Missing, null);
        byte[] plaintext;
        try
        {
            var envelope = SecureEnvelope.Decode(File.ReadAllBytes(path));
            plaintext = SecureEnvelopeCodec.Decrypt(_masterKey, purpose, envelope);
        }
        catch (CryptographicException)
        {
            return (CountryFileKind.AuthFailed, null);
        }
        catch (EnvelopeFormatException)
        {
            return (CountryFileKind.Corrupt, null);
        }
        catch (IOException)
        {
            return (CountryFileKind.IoFailed, null);
        }
        catch (UnauthorizedAccessException)
        {
            return (CountryFileKind.IoFailed, null);
        }

        var (result, doc) = CountryChoiceDocument.TryParse(plaintext);
        return result switch
        {
            CountryChoiceDocument.ParseResult.Ok => (CountryFileKind.Ok, doc),
            CountryChoiceDocument.ParseResult.UnknownVersion => (CountryFileKind.UnknownVersion, null),
            _ => (CountryFileKind.Corrupt, null),
        };
    }

    /// <summary>
    /// The persisted choice for one country. Never throws for a bad file: every failure is a non-choice state. Throws only for an invalid
    /// <paramref name="country"/> id (a programming error).
    /// </summary>
    public CountryChoiceRead LoadCountryChoice(string country) => LoadChoice(_countryChoicePath, StorePurpose.CountrySettings, country);

    private CountryChoiceRead LoadChoice(string path, string purpose, string country)
    {
        if (!CountryChoiceDocument.IsValidCountryId(country))
            throw new ArgumentException("The country id must be 1-32 ASCII letters or digits, starting with a letter.", nameof(country));

        lock (CountryLocks.GetOrAdd(path, _ => new object()))
        {
            var (kind, doc) = ReadCountryFile(path, purpose);
            switch (kind)
            {
                case CountryFileKind.Missing: return new(CountryChoiceState.Missing, 0);
                case CountryFileKind.Corrupt: return new(CountryChoiceState.Corrupt, 0);
                case CountryFileKind.UnknownVersion: return new(CountryChoiceState.UnknownVersion, 0);
                case CountryFileKind.AuthFailed:
                case CountryFileKind.IoFailed: return new(CountryChoiceState.Unauthenticated, 0);
            }

            long highest = CountryHighestRevision.GetOrAdd(path, 0);
            if (doc!.Revision < highest) return new(CountryChoiceState.Unauthenticated, doc.Revision);   // a record older than one already seen
            CountryHighestRevision[path] = doc.Revision;

            var choice = doc.Choices.FirstOrDefault(c => string.Equals(c.Country, country, StringComparison.OrdinalIgnoreCase));
            if (choice is null) return new(CountryChoiceState.Missing, doc.Revision);
            if (choice.Origin != CountryChoiceDocument.UserActionOrigin) return new(CountryChoiceState.Unauthenticated, doc.Revision);
            return new(choice.Enabled ? CountryChoiceState.On : CountryChoiceState.Off, doc.Revision);
        }
    }

    /// <summary>
    /// Records one country's choice. The caller states WHERE the choice came from; only <see cref="CountryChoiceOrigin.SettingsUserAction"/> is
    /// accepted, and it must be passed only from a real user action in the Settings UI -- the store cannot check that. Other countries' choices are kept.
    /// Throws (and leaves the existing record untouched) when there is no usable key, when the existing record is of a newer format version, or when the
    /// file cannot be read or replaced (an IO or access exception).
    /// </summary>
    public void SaveCountryChoice(string country, bool enabled, CountryChoiceOrigin origin) =>
        SaveChoice(_countryChoicePath, StorePurpose.CountrySettings, country, enabled, origin);

    private void SaveChoice(string path, string purpose, string country, bool enabled, CountryChoiceOrigin origin)
    {
        if (!CountryChoiceDocument.IsValidCountryId(country))
            throw new ArgumentException("The country id must be 1-32 ASCII letters or digits, starting with a letter.", nameof(country));
        if (origin != CountryChoiceOrigin.SettingsUserAction)
            throw new ArgumentException("Only a choice that came from a Settings user action can be recorded.", nameof(origin));
        if (_masterKey is null)
        {
            throw new InvalidOperationException(
                "Cannot save: the master encryption key is currently unavailable. Writing under a temporary or replacement key is never permitted.");
        }

        lock (CountryLocks.GetOrAdd(path, _ => new object()))
        {
            var (kind, doc) = ReadCountryFile(path, purpose);
            long highest = CountryHighestRevision.GetOrAdd(path, 0);
            List<CountryChoiceDocument.Choice> choices;
            long baseRevision;

            switch (kind)
            {
                case CountryFileKind.UnknownVersion:
                    throw new InvalidOperationException("The existing country settings are of a newer format version and are never overwritten.");
                case CountryFileKind.IoFailed:
                    throw new IOException("The existing country settings could not be read, so they were not replaced.");
                case CountryFileKind.Ok when doc!.Revision >= highest:
                    choices = doc.Choices.ToList();
                    baseRevision = doc.Revision;
                    break;
                default:
                    // missing, corrupt, failed authentication, or older than one already seen: nothing in it can be trusted, so it is replaced
                    choices = [];
                    baseRevision = highest;
                    break;
            }

            choices.RemoveAll(c => string.Equals(c.Country, country, StringComparison.OrdinalIgnoreCase));
            choices.Add(new CountryChoiceDocument.Choice(country, enabled, CountryChoiceDocument.UserActionOrigin));
            long revision = baseRevision + 1;

            var plaintext = CountryChoiceDocument.Serialize(new CountryChoiceDocument.Doc(revision, choices));
            var envelope = SecureEnvelopeCodec.Encrypt(_masterKey, purpose, plaintext);
            AtomicFileWriter.WriteAllBytes(path, envelope.Encode());   // throws leave the old file as it was
            CountryHighestRevision[path] = revision;
        }
    }

    // ----------------------------------------------------------------------------------------------------------------
    // PRIVON 0.4.0 P1 -- the ACTIVATION record of a country pack: "the user explicitly turned this country's detection on". It is a separate file
    // (country-activation.bin) with its own purpose, built on the SAME envelope, document format, origin check, revision rule and atomic replace as the
    // country choice above (the two share one implementation, parameterized by file and purpose), so a damaged choice file never says anything about
    // the activation and the reverse. A record reads as On only when it is an INTACT, known-version, user-action record; Missing means "never
    // activated"; anything else (Corrupt / Unauthenticated / UnknownVersion) is an activation that CANNOT BE VERIFIED -- never an Off and never an On.
    // An activation is only ever recorded as On: there is no "deactivate" (turning detection off afterwards is the country choice's Off).
    // LIMIT: as for the choice, the origin marker is a claim by the writing code, not proof of a person.
    // ----------------------------------------------------------------------------------------------------------------

    /// <summary>The persisted activation of one country pack: <see cref="CountryChoiceState.On"/> only for an intact user-action record, <see cref="CountryChoiceState.Missing"/> when never activated, and any other state when the record cannot be verified.</summary>
    public CountryChoiceRead LoadCountryActivation(string country) => LoadChoice(_countryActivationPath, StorePurpose.CountryActivation, country);

    /// <summary>Records that the user explicitly activated this country pack. Same refusals as <see cref="SaveCountryChoice"/> (no usable key, a newer-format record, an IO failure, an origin other than a Settings user action).</summary>
    public void SaveCountryActivation(string country, CountryChoiceOrigin origin) =>
        SaveChoice(_countryActivationPath, StorePurpose.CountryActivation, country, true, origin);

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
