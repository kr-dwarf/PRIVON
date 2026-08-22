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
    private readonly byte[] _masterKey;

    private PrivonLocalStore(string rootDirectory, byte[] masterKey)
    {
        _settingsPath = Path.Combine(rootDirectory, "settings.bin");
        _trustedPath = Path.Combine(rootDirectory, "trusted-public-info.bin");
        _exceptionsPath = Path.Combine(rootDirectory, "exceptions.bin");
        _masterKey = masterKey;
    }

    public static PrivonLocalStore OpenOrCreate(string rootDirectory)
    {
        Directory.CreateDirectory(rootDirectory);
        var keyPath = Path.Combine(rootDirectory, "master.key");
        var masterKey = MasterKeyStore.LoadOrCreate(keyPath);
        return new PrivonLocalStore(rootDirectory, masterKey);
    }

    public PrivonSettings LoadSettings() =>
        LoadOrDefault(_settingsPath, StorePurpose.Settings, PrivonSettings.SafeDefault,
            b => JsonSerializer.Deserialize<PrivonSettings>(b) ?? PrivonSettings.SafeDefault);

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
    }

    private void Save(string path, string purpose, byte[] plaintext)
    {
        var envelope = SecureEnvelopeCodec.Encrypt(_masterKey, purpose, plaintext);
        AtomicFileWriter.WriteAllBytes(path, envelope.Encode());
    }
}
