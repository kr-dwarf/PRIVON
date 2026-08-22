using System.Text;
using Privon.Storage;

namespace Privon.Storage.Tests;

// Phase 2R.2 -- Typed Storage Schema v2. Confirms the PiiTypeId schema addition round-trips
// correctly, that old (pre-PiiTypeId) v1-encrypted payloads become unreadable rather than
// silently reinterpreted, and that structurally malformed v2 entries never come back as
// trusted data. Nothing here assumes System.Text.Json behavior -- every claim about
// missing/null/empty/whitespace field handling is exercised directly against the real store.
// All values are synthetic fixtures, never real PII.
public class TypedSchemaV2Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PrivonStorageTests_" + Guid.NewGuid().ToString("N"));

    // The pre-Phase-2R.2 purpose strings, hardcoded here (not read from StorePurpose, which
    // now holds the v2 values) to simulate genuinely old, already-encrypted-under-v1 files.
    private const string LegacyExceptionsPurpose = "PRIVON.Exceptions.v1";
    private const string LegacyTrustedPurpose = "PRIVON.TrustedPublicInfo.v1";

    private byte[] MasterKey()
    {
        // Same lazy-create-or-load path PrivonLocalStore itself uses, so a payload encrypted
        // here decrypts under the exact same key the store will use to read it back.
        Directory.CreateDirectory(_root);
        return MasterKeyStore.LoadOrCreate(Path.Combine(_root, "master.key"));
    }

    private void WriteEncryptedPayload(string fileName, string purpose, string json)
    {
        var envelope = SecureEnvelopeCodec.Encrypt(MasterKey(), purpose, Encoding.UTF8.GetBytes(json));
        AtomicFileWriter.WriteAllBytes(Path.Combine(_root, fileName), envelope.Encode());
    }

    // ---- 1/3/4. typed ExceptionEntry save/load roundtrip, PiiTypeId + Value preserved ----
    [Fact]
    public void ExceptionEntry_RoundTrips_PiiTypeIdAndValuePreserved()
    {
        var store = PrivonLocalStore.OpenOrCreate(_root);
        var entries = new List<ExceptionEntry> { new("Phone", "SYNTHETIC-EXC-001"), new("Email", "SYNTHETIC-EXC-002") };

        store.SaveExceptions(entries);
        var loaded = store.LoadExceptions();

        Assert.Equal(entries, loaded);
        Assert.Equal("Phone", loaded[0].PiiTypeId);
        Assert.Equal("SYNTHETIC-EXC-001", loaded[0].Value);
    }

    // ---- 2/3/4. typed TrustedPublicInfoEntry save/load roundtrip, PiiTypeId + Value preserved ----
    [Fact]
    public void TrustedPublicInfoEntry_RoundTrips_PiiTypeIdAndValuePreserved()
    {
        var store = PrivonLocalStore.OpenOrCreate(_root);
        var entries = new List<TrustedPublicInfoEntry> { new("Phone", "SYNTHETIC-TRUST-001") };

        store.SaveTrustedPublicInfo(entries);
        var loaded = store.LoadTrustedPublicInfo();

        Assert.Equal(entries, loaded);
        Assert.Equal("Phone", loaded[0].PiiTypeId);
        Assert.Equal("SYNTHETIC-TRUST-001", loaded[0].Value);
    }

    // ---- 5/17. Exceptions and Trusted purposes remain cryptographically separated under v2 ----
    [Fact]
    public void CrossStoreDecryption_StillFails_UnderV2Purposes()
    {
        var store = PrivonLocalStore.OpenOrCreate(_root);
        store.SaveExceptions([new ExceptionEntry("Phone", "SYNTHETIC-EXC-001")]);

        var envelope = SecureEnvelope.Decode(File.ReadAllBytes(Path.Combine(_root, "exceptions.bin")));
        var masterKey = MasterKeyStore.LoadOrCreate(Path.Combine(_root, "master.key"));

        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() =>
            SecureEnvelopeCodec.Decrypt(masterKey, StorePurpose.TrustedPublicInfo, envelope));
    }

    // ---- 6/7. a genuinely old v1-encrypted Exceptions payload (no PiiTypeId field at all,
    // encrypted under the pre-2R.2 purpose string) is not reused by the v2 reader ----
    [Fact]
    public void LegacyV1ExceptionsPayload_NotTrustedByV2Reader_ReturnsEmpty()
    {
        WriteEncryptedPayload("exceptions.bin", LegacyExceptionsPurpose, """[{"Value":"SYNTHETIC-LEGACY-EXC"}]""");

        var store = PrivonLocalStore.OpenOrCreate(_root);
        var loaded = store.LoadExceptions();

        Assert.Empty(loaded);
    }

    // ---- 8. same for the Trusted store ----
    [Fact]
    public void LegacyV1TrustedPayload_NotTrustedByV2Reader_ReturnsEmpty()
    {
        WriteEncryptedPayload("trusted-public-info.bin", LegacyTrustedPurpose, """[{"Value":"SYNTHETIC-LEGACY-TRUST"}]""");

        var store = PrivonLocalStore.OpenOrCreate(_root);
        var loaded = store.LoadTrustedPublicInfo();

        Assert.Empty(loaded);
    }

    // ---- 9. corrupted v2 Exceptions payload -> empty ----
    [Fact]
    public void CorruptedV2ExceptionsPayload_ReturnsEmpty()
    {
        var store = PrivonLocalStore.OpenOrCreate(_root);
        store.SaveExceptions([new ExceptionEntry("Phone", "SYNTHETIC-EXC-001")]);

        var path = Path.Combine(_root, "exceptions.bin");
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0xFF; // flip the last ciphertext byte
        File.WriteAllBytes(path, bytes);

        Assert.Empty(store.LoadExceptions());
    }

    // ---- 10. corrupted v2 TrustedPublicInfo payload -> empty ----
    [Fact]
    public void CorruptedV2TrustedPayload_ReturnsEmpty()
    {
        var store = PrivonLocalStore.OpenOrCreate(_root);
        store.SaveTrustedPublicInfo([new TrustedPublicInfoEntry("Phone", "SYNTHETIC-TRUST-001")]);

        var path = Path.Combine(_root, "trusted-public-info.bin");
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        Assert.Empty(store.LoadTrustedPublicInfo());
    }

    // ---- 11. missing PiiTypeId field entirely (valid v2 purpose, old-shaped JSON) ----
    [Fact]
    public void V2Payload_MissingPiiTypeIdField_FailsClosed()
    {
        WriteEncryptedPayload("exceptions.bin", StorePurpose.Exceptions, """[{"Value":"SYNTHETIC-EXC"}]""");

        var store = PrivonLocalStore.OpenOrCreate(_root);
        Assert.Empty(store.LoadExceptions());
    }

    // ---- 12. explicit null PiiTypeId ----
    [Fact]
    public void V2Payload_NullPiiTypeId_FailsClosed()
    {
        WriteEncryptedPayload("exceptions.bin", StorePurpose.Exceptions, """[{"PiiTypeId":null,"Value":"SYNTHETIC-EXC"}]""");

        var store = PrivonLocalStore.OpenOrCreate(_root);
        Assert.Empty(store.LoadExceptions());
    }

    // ---- 13. empty-string PiiTypeId ----
    [Fact]
    public void V2Payload_EmptyPiiTypeId_FailsClosed()
    {
        WriteEncryptedPayload("trusted-public-info.bin", StorePurpose.TrustedPublicInfo, """[{"PiiTypeId":"","Value":"SYNTHETIC-TRUST"}]""");

        var store = PrivonLocalStore.OpenOrCreate(_root);
        Assert.Empty(store.LoadTrustedPublicInfo());
    }

    // ---- 14. whitespace-only PiiTypeId ----
    [Fact]
    public void V2Payload_WhitespacePiiTypeId_FailsClosed()
    {
        WriteEncryptedPayload("trusted-public-info.bin", StorePurpose.TrustedPublicInfo, """[{"PiiTypeId":"   ","Value":"SYNTHETIC-TRUST"}]""");

        var store = PrivonLocalStore.OpenOrCreate(_root);
        Assert.Empty(store.LoadTrustedPublicInfo());
    }

    // A malformed entry invalidates the WHOLE store, not just itself -- a well-formed sibling
    // entry in the same file must not be returned either.
    [Fact]
    public void V2Payload_OneMalformedEntryAmongValidOnes_WholeStoreFailsClosed()
    {
        WriteEncryptedPayload("exceptions.bin", StorePurpose.Exceptions,
            """[{"PiiTypeId":"Phone","Value":"SYNTHETIC-EXC-OK"},{"PiiTypeId":"","Value":"SYNTHETIC-EXC-BAD"}]""");

        var store = PrivonLocalStore.OpenOrCreate(_root);
        Assert.Empty(store.LoadExceptions());
    }

    // ---- 15. malformed JSON (not valid JSON at all) -> empty ----
    [Fact]
    public void MalformedJson_ReturnsEmpty()
    {
        WriteEncryptedPayload("exceptions.bin", StorePurpose.Exceptions, "{ this is not valid json ][");

        var store = PrivonLocalStore.OpenOrCreate(_root);
        Assert.Empty(store.LoadExceptions());
    }

    // ---- 16. wrong purpose/AAD (well-formed v2 JSON, wrong purpose string) -> empty ----
    [Fact]
    public void WrongPurpose_ReturnsEmpty()
    {
        WriteEncryptedPayload("exceptions.bin", "PRIVON.SomeOtherPurpose.v1", """[{"PiiTypeId":"Phone","Value":"SYNTHETIC-EXC"}]""");

        var store = PrivonLocalStore.OpenOrCreate(_root);
        Assert.Empty(store.LoadExceptions());
    }

    // ---- Save-side validation: malformed entries are rejected before they can ever be
    // persisted, for both stores. ----
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SaveExceptions_RejectsMalformedPiiTypeId(string? piiTypeId)
    {
        var store = PrivonLocalStore.OpenOrCreate(_root);
        Assert.Throws<ArgumentException>(() => store.SaveExceptions([new ExceptionEntry(piiTypeId!, "SYNTHETIC-EXC")]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SaveTrustedPublicInfo_RejectsMalformedValue(string? value)
    {
        var store = PrivonLocalStore.OpenOrCreate(_root);
        Assert.Throws<ArgumentException>(() => store.SaveTrustedPublicInfo([new TrustedPublicInfoEntry("Phone", value!)]));
    }

    // ---- 18. synthetic fixture only -- every value above is a generated placeholder, never
    // real PII or a real stored user value. ----

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
