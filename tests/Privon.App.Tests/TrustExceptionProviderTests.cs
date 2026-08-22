using System.Reflection;
using System.Text;
using Privon.App;
using Privon.Detection;
using Privon.Storage;

namespace Privon.App.Tests;

// Phase 3B STEP6 -- TrustExceptionProvider regression. Uses a REAL PrivonLocalStore against a
// temp directory (same technique as Privon.Storage.Tests.TypedSchemaV2Tests) rather than a fake
// -- Storage's own API is already synchronous/deterministic/test-friendly, so a fake Storage seam
// would just be indirection with no benefit. No mocking framework. All values are synthetic
// fixtures, never real PII.
public class TrustExceptionProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PrivonAppBridgeTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private PrivonLocalStore OpenStore() => PrivonLocalStore.OpenOrCreate(_root);

    // ==================================================================
    // REAL STORAGE MAPPING (section 17, items 1-8)
    // ==================================================================

    // ---- 1. one trusted Phone entry -> one TrustedPublicValue ----
    [Fact]
    public void Trusted_SinglePhoneEntry_MapsToOneTrustedPublicValue()
    {
        var store = OpenStore();
        store.SaveTrustedPublicInfo([new TrustedPublicInfoEntry("Phone", "01012345678")]);

        var snapshot = new TrustExceptionProvider(store).Load();

        Assert.Single(snapshot.TrustedPublic);
        Assert.Empty(snapshot.Exceptions);
    }

    // ---- 2. one trusted Email entry -> correct PiiType/value ----
    [Fact]
    public void Trusted_SingleEmailEntry_MapsCorrectPiiTypeAndValue()
    {
        var store = OpenStore();
        store.SaveTrustedPublicInfo([new TrustedPublicInfoEntry("Email", "user@example.test")]);

        var snapshot = new TrustExceptionProvider(store).Load();

        var mapped = Assert.Single(snapshot.TrustedPublic);
        Assert.Equal(PiiType.Email, mapped.PiiType);
        Assert.Equal(new CanonicalValue(PiiType.Email, "user@example.test"), mapped.CanonicalValue);
    }

    // ---- 3. one Exception entry -> one AmbiguousExceptionValue ----
    [Fact]
    public void Exception_SingleEntry_MapsToOneAmbiguousExceptionValue()
    {
        var store = OpenStore();
        store.SaveExceptions([new ExceptionEntry("GpsCoordinate", "37.5,127.0")]);

        var snapshot = new TrustExceptionProvider(store).Load();

        Assert.Single(snapshot.Exceptions);
        Assert.Empty(snapshot.TrustedPublic);
    }

    // ---- 4. multiple mixed trusted entries -> all recognized preserved ----
    [Fact]
    public void Trusted_MultipleMixedEntries_AllRecognizedPreserved()
    {
        var store = OpenStore();
        store.SaveTrustedPublicInfo(
        [
            new TrustedPublicInfoEntry("Phone", "01012345678"),
            new TrustedPublicInfoEntry("Email", "user@example.test"),
            new TrustedPublicInfoEntry("IpAddress", "192.0.2.1"),
        ]);

        var snapshot = new TrustExceptionProvider(store).Load();

        Assert.Equal(3, snapshot.TrustedPublic.Count);
        Assert.Contains(snapshot.TrustedPublic, v => v.PiiType == PiiType.Phone);
        Assert.Contains(snapshot.TrustedPublic, v => v.PiiType == PiiType.Email);
        Assert.Contains(snapshot.TrustedPublic, v => v.PiiType == PiiType.IpAddress);
    }

    // ---- 5. multiple mixed exception entries -> all recognized preserved ----
    [Fact]
    public void Exception_MultipleMixedEntries_AllRecognizedPreserved()
    {
        var store = OpenStore();
        store.SaveExceptions(
        [
            new ExceptionEntry("GpsCoordinate", "37.5,127.0"),
            new ExceptionEntry("MacAddress", "00:11:22:33:44:55"),
        ]);

        var snapshot = new TrustExceptionProvider(store).Load();

        Assert.Equal(2, snapshot.Exceptions.Count);
        Assert.Contains(snapshot.Exceptions, v => v.PiiType == PiiType.GpsCoordinate);
        Assert.Contains(snapshot.Exceptions, v => v.PiiType == PiiType.MacAddress);
    }

    // ---- 6. trusted + exception stores loaded together ----
    [Fact]
    public void TrustedAndException_LoadedTogether_BothPopulated()
    {
        var store = OpenStore();
        store.SaveTrustedPublicInfo([new TrustedPublicInfoEntry("Phone", "01012345678")]);
        store.SaveExceptions([new ExceptionEntry("GpsCoordinate", "37.5,127.0")]);

        var snapshot = new TrustExceptionProvider(store).Load();

        Assert.Single(snapshot.TrustedPublic);
        Assert.Single(snapshot.Exceptions);
    }

    // ---- 7. canonical Value preserved string-exactly -- no trim/normalize/re-detect ----
    [Fact]
    public void CanonicalValue_PreservedExactly_NoReNormalization()
    {
        var store = OpenStore();
        const string oddlyFormatted = " 010-1234-5678 "; // deliberately not phone-canonical form
        store.SaveTrustedPublicInfo([new TrustedPublicInfoEntry("Phone", oddlyFormatted)]);

        var snapshot = new TrustExceptionProvider(store).Load();

        Assert.Equal(oddlyFormatted, Assert.Single(snapshot.TrustedPublic).CanonicalValue.Value);
    }

    // ---- 8. outer PiiType wrapper always matches CanonicalValue.PiiType ----
    [Fact]
    public void OuterPiiType_AlwaysMatchesCanonicalValuePiiType()
    {
        var store = OpenStore();
        store.SaveTrustedPublicInfo([new TrustedPublicInfoEntry("CardNumber", "4111111111111111")]);
        store.SaveExceptions([new ExceptionEntry("BankAccountNumber", "110-123-456789")]);

        var snapshot = new TrustExceptionProvider(store).Load();

        var trusted = Assert.Single(snapshot.TrustedPublic);
        Assert.Equal(trusted.PiiType, trusted.CanonicalValue.PiiType);
        var exception = Assert.Single(snapshot.Exceptions);
        Assert.Equal(exception.PiiType, exception.CanonicalValue.PiiType);
    }

    // ==================================================================
    // UNKNOWN PIITYPE (section 18)
    // ==================================================================

    [Fact]
    public void RecognizedTrustedPlusUnknownTrusted_UnknownSkipped_RecognizedRemains()
    {
        var store = OpenStore();
        store.SaveTrustedPublicInfo(
        [
            new TrustedPublicInfoEntry("Phone", "01012345678"),
            new TrustedPublicInfoEntry("FutureType", "SYNTHETIC-UNKNOWN"),
        ]);

        var snapshot = new TrustExceptionProvider(store).Load();

        var mapped = Assert.Single(snapshot.TrustedPublic);
        Assert.Equal(PiiType.Phone, mapped.PiiType);
    }

    [Fact]
    public void RecognizedExceptionPlusUnknownException_UnknownSkipped_RecognizedRemains()
    {
        var store = OpenStore();
        store.SaveExceptions(
        [
            new ExceptionEntry("GpsCoordinate", "37.5,127.0"),
            new ExceptionEntry("FutureType", "SYNTHETIC-UNKNOWN"),
        ]);

        var snapshot = new TrustExceptionProvider(store).Load();

        var mapped = Assert.Single(snapshot.Exceptions);
        Assert.Equal(PiiType.GpsCoordinate, mapped.PiiType);
    }

    [Fact]
    public void UnknownOnlyTrusted_EmptyTrustedMappedList()
    {
        var store = OpenStore();
        store.SaveTrustedPublicInfo([new TrustedPublicInfoEntry("FutureType", "SYNTHETIC-UNKNOWN")]);

        var snapshot = new TrustExceptionProvider(store).Load();

        Assert.Empty(snapshot.TrustedPublic);
    }

    [Fact]
    public void UnknownOnlyException_EmptyExceptionMappedList()
    {
        var store = OpenStore();
        store.SaveExceptions([new ExceptionEntry("FutureType", "SYNTHETIC-UNKNOWN")]);

        var snapshot = new TrustExceptionProvider(store).Load();

        Assert.Empty(snapshot.Exceptions);
    }

    [Fact]
    public void UnknownPiiTypeId_NeverThrows()
    {
        var store = OpenStore();
        store.SaveTrustedPublicInfo([new TrustedPublicInfoEntry("FutureType", "SYNTHETIC-UNKNOWN")]);
        store.SaveExceptions([new ExceptionEntry("AnotherFutureType", "SYNTHETIC-UNKNOWN")]);

        var exception = Record.Exception(() => new TrustExceptionProvider(store).Load());

        Assert.Null(exception);
    }

    // ==================================================================
    // SAFE-EMPTY STORAGE FAILURE (section 19)
    // ==================================================================

    [Fact]
    public void MissingStore_EmptySnapshot()
    {
        var store = OpenStore(); // nothing ever saved

        var snapshot = new TrustExceptionProvider(store).Load();

        Assert.Empty(snapshot.TrustedPublic);
        Assert.Empty(snapshot.Exceptions);
    }

    [Fact]
    public void CorruptedTrustedStore_EmptyTrustedList()
    {
        var store = OpenStore();
        store.SaveTrustedPublicInfo([new TrustedPublicInfoEntry("Phone", "01012345678")]);
        CorruptLastByte(Path.Combine(_root, "trusted-public-info.bin"));

        var snapshot = new TrustExceptionProvider(store).Load();

        Assert.Empty(snapshot.TrustedPublic);
    }

    [Fact]
    public void CorruptedExceptionStore_EmptyExceptionList()
    {
        var store = OpenStore();
        store.SaveExceptions([new ExceptionEntry("GpsCoordinate", "37.5,127.0")]);
        CorruptLastByte(Path.Combine(_root, "exceptions.bin"));

        var snapshot = new TrustExceptionProvider(store).Load();

        Assert.Empty(snapshot.Exceptions);
    }

    // ---- authentication/decrypt failure: a well-formed v2 JSON payload encrypted under the
    // WRONG purpose fails AES-GCM authentication under the real one -- same technique as
    // TypedSchemaV2Tests.WrongPurpose_ReturnsEmpty, reimplemented here at the App layer to prove
    // the bridge (not just Storage in isolation) ends up with an empty mapped list. ----
    [Fact]
    public void AuthenticationFailure_WrongPurpose_EmptyTrustedList()
    {
        Directory.CreateDirectory(_root);
        var masterKey = MasterKeyStore.LoadOrCreate(Path.Combine(_root, "master.key"));
        var envelope = SecureEnvelopeCodec.Encrypt(masterKey, "PRIVON.SomeOtherPurpose.v1",
            Encoding.UTF8.GetBytes("""[{"PiiTypeId":"Phone","Value":"SYNTHETIC-EXC"}]"""));
        AtomicFileWriter.WriteAllBytes(Path.Combine(_root, "trusted-public-info.bin"), envelope.Encode());

        var store = OpenStore();
        var snapshot = new TrustExceptionProvider(store).Load();

        Assert.Empty(snapshot.TrustedPublic);
    }

    private static void CorruptLastByte(string path)
    {
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
    }

    // ==================================================================
    // DIRECT DIAGNOSTIC REGRESSION (section 21)
    // ==================================================================

    [Fact]
    public void TrustExceptionSnapshot_ToString_DoesNotContainSentinel()
    {
        const string sentinel = "BRIDGE-CANONICAL-SENTINEL-731942";
        var store = OpenStore();
        store.SaveTrustedPublicInfo([new TrustedPublicInfoEntry("Phone", sentinel)]);
        store.SaveExceptions([new ExceptionEntry("Email", sentinel)]);

        var snapshot = new TrustExceptionProvider(store).Load();

        Assert.DoesNotContain(sentinel, snapshot.ToString());
        Assert.DoesNotContain(sentinel, $"{snapshot}");
    }

    [Fact]
    public void TrustExceptionSnapshot_ToString_ContainsOnlyCounts()
    {
        var store = OpenStore();
        store.SaveTrustedPublicInfo([new TrustedPublicInfoEntry("Phone", "01012345678")]);
        store.SaveExceptions(
        [
            new ExceptionEntry("GpsCoordinate", "37.5,127.0"),
            new ExceptionEntry("MacAddress", "00:11:22:33:44:55"),
        ]);

        var snapshot = new TrustExceptionProvider(store).Load();
        string rendered = snapshot.ToString();

        Assert.Contains("TrustedPublicCount", rendered);
        Assert.Contains("1", rendered);
        Assert.Contains("ExceptionCount", rendered);
        Assert.Contains("2", rendered);
    }

    [Fact]
    public void MappedValues_Interpolation_DoesNotContainSentinel()
    {
        const string sentinel = "BRIDGE-CANONICAL-SENTINEL-731942";
        var store = OpenStore();
        store.SaveTrustedPublicInfo([new TrustedPublicInfoEntry("Phone", sentinel)]);
        store.SaveExceptions([new ExceptionEntry("Email", sentinel)]);

        var snapshot = new TrustExceptionProvider(store).Load();

        Assert.DoesNotContain(sentinel, $"{Assert.Single(snapshot.TrustedPublic)}");
        Assert.DoesNotContain(sentinel, $"{Assert.Single(snapshot.Exceptions)}");
    }

    // ==================================================================
    // STRUCTURAL BOUNDARY (section 27)
    // ==================================================================

    [Theory]
    [InlineData(typeof(ITrustExceptionProvider))]
    [InlineData(typeof(TrustExceptionProvider))]
    [InlineData(typeof(TrustExceptionSnapshot))]
    public void BridgeTypes_AreNotPublic(Type type)
    {
        Assert.False(type.IsPublic);
    }

    // ---- the provider retains no sensitive list/string field -- the only persistent field is
    // the injected PrivonLocalStore itself ----
    [Fact]
    public void TrustExceptionProvider_HasNoSensitiveInstanceField()
    {
        var fields = typeof(TrustExceptionProvider)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        var forbiddenTypes = new[]
        {
            typeof(string), typeof(CanonicalValue),
            typeof(IReadOnlyList<TrustedPublicValue>), typeof(IReadOnlyList<AmbiguousExceptionValue>),
            typeof(IReadOnlyList<TrustedPublicInfoEntry>), typeof(IReadOnlyList<ExceptionEntry>),
            typeof(TrustExceptionSnapshot), typeof(List<TrustedPublicValue>), typeof(List<AmbiguousExceptionValue>),
        };

        Assert.DoesNotContain(fields, f => forbiddenTypes.Contains(f.FieldType));
        Assert.Contains(fields, f => f.FieldType == typeof(PrivonLocalStore));
    }

    // ---- Storage and Detection remain mutually independent -- no new cross-reference was
    // introduced by this bridge (the bridge lives entirely in Privon.App) ----
    [Fact]
    public void Storage_And_Detection_RemainMutuallyIndependent()
    {
        var storageAssembly = typeof(PrivonLocalStore).Assembly;
        var detectionAssembly = typeof(CanonicalValue).Assembly;

        Assert.DoesNotContain(storageAssembly.GetReferencedAssemblies(), a => a.Name == "Privon.Detection");
        Assert.DoesNotContain(detectionAssembly.GetReferencedAssemblies(), a => a.Name == "Privon.Storage");
    }
}
