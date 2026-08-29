using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Privon.Storage.Tests;

// PRIVON v0.2.1 Gate 3A -- ProtectionCategorySettings persistence regression. Reuses the existing
// settings.bin / StorePurpose.Settings / PrivonLocalStore / encrypted-envelope / atomic-save /
// LoadOrDefault infrastructure verbatim -- no new file, no new StorePurpose, no new persistence
// subsystem. Categories is an ADDITIVE property on the existing PrivonSettings record.
//
// SCHEMA_SUPPORTED_DETECTOR_NOT_YET_IMPLEMENTED: NameEnabled/AddressEnabled/CompanyEnabled persist
// and round-trip correctly today even though no detector produces a candidate for any of the
// three yet (see CategoryPolicyEvaluator -- only PhoneEnabled/EmailEnabled currently gate any real
// candidate). This file proves persistence only, never a behavioral/detector claim for those three.
public class ProtectionCategorySettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PrivonCategorySettingsTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private PrivonLocalStore OpenStore() => PrivonLocalStore.OpenOrCreate(_root);

    // ==================================================================
    // CAT-001 -- missing settings file -> Categories defaults to AllOn.
    // ==================================================================
    [Fact]
    public void Cat001_MissingSettingsFile_CategoriesDefaultsToAllOn()
    {
        var store = OpenStore(); // nothing ever saved

        var loaded = store.LoadSettings();

        Assert.Equal(ProtectionCategorySettings.AllOn, loaded.Categories);
    }

    // ==================================================================
    // CAT-002 -- persistence round-trip, all 5 booleans, a genuinely mixed (non-default) pattern.
    // ==================================================================
    [Fact]
    public void Cat002_PersistenceRoundTrip_AllFiveBooleansPreservedExactly()
    {
        var store = OpenStore();
        var categories = new ProtectionCategorySettings(
            NameEnabled: false, PhoneEnabled: true, EmailEnabled: false, AddressEnabled: true, CompanyEnabled: false);
        store.SaveSettings(new PrivonSettings(ProtectionEnabled: true, Categories: categories));

        var loaded = store.LoadSettings();

        Assert.Equal(categories, loaded.Categories);
        Assert.False(loaded.Categories!.NameEnabled);
        Assert.True(loaded.Categories.PhoneEnabled);
        Assert.False(loaded.Categories.EmailEnabled);
        Assert.True(loaded.Categories.AddressEnabled);
        Assert.False(loaded.Categories.CompanyEnabled);
    }

    // ==================================================================
    // CAT-003 -- corrupt/tampered settings -> safe fallback -> Categories == AllOn.
    // Same technique as CorruptionFallbackTests.TamperedCiphertext_FallsBackToSafeDefault.
    // ==================================================================
    [Fact]
    public void Cat003_CorruptSettings_FallsBackToAllOn()
    {
        var settingsPath = Path.Combine(_root, "settings.bin");
        var store = OpenStore();
        store.SaveSettings(new PrivonSettings(
            ProtectionEnabled: true,
            Categories: new ProtectionCategorySettings(false, false, false, false, false)));

        var bytes = File.ReadAllBytes(settingsPath);
        bytes[^1] ^= 0xFF; // flip the last ciphertext byte -- AES-GCM authentication fails
        File.WriteAllBytes(settingsPath, bytes);

        var loaded = store.LoadSettings();

        Assert.Equal(ProtectionCategorySettings.AllOn, loaded.Categories);
        Assert.Equal(PrivonSettings.SafeDefault, loaded);
    }

    // ---- old persisted settings WITHOUT a Categories property at all (pre-feature JSON shape) --
    // Categories must resolve to AllOn, never null, never "no categories". Simulates a real
    // pre-migration payload by encrypting hand-written JSON that omits the field entirely, rather
    // than assuming JsonSerializer's own default-parameter binding behavior. ----
    [Fact]
    public void Cat001_OldFormatJsonWithoutCategoriesProperty_ResolvesToAllOn()
    {
        Directory.CreateDirectory(_root);
        var keyPath = Path.Combine(_root, "master.key");
        var masterKey = MasterKeyStore.LoadOrCreate(keyPath);
        var oldFormatJson = """{"ProtectionEnabled":true,"PausedUntilUtc":null}""";
        var envelope = SecureEnvelopeCodec.Encrypt(
            masterKey, StorePurpose.Settings, System.Text.Encoding.UTF8.GetBytes(oldFormatJson));
        AtomicFileWriter.WriteAllBytes(Path.Combine(_root, "settings.bin"), envelope.Encode());

        var store = OpenStore();
        var loaded = store.LoadSettings();

        Assert.Equal(ProtectionCategorySettings.AllOn, loaded.Categories);
        Assert.True(loaded.ProtectionEnabled); // the rest of the old payload still loads correctly
    }

    // ==================================================================
    // CAT-013 -- settings.bin exists but is unreadable (access-forbidden) -> LoadSettings does
    // NOT throw -> Categories == AllOn. Exercises the REAL production read path (LoadSettings ->
    // LoadOrDefault -> File.ReadAllBytes), through a genuine Windows ACL Deny rule on the file --
    // not a fake that directly throws UnauthorizedAccessException. The deny rule is scoped to the
    // CURRENT user only, on a file inside this test's own disposable temp directory (no admin
    // rights required, nothing outside the temp directory is ever touched), and is always removed
    // in `finally` before the directory itself is deleted -- no ACL/permission state survives the
    // test regardless of pass/fail.
    // ==================================================================
    [Fact]
    public void Cat013_UnreadableSettingsFile_LoadSettingsDoesNotThrow_CategoriesResolveToAllOn()
    {
        var store = OpenStore();
        store.SaveSettings(new PrivonSettings(
            ProtectionEnabled: true, Categories: new ProtectionCategorySettings(false, false, false, false, false)));

        var settingsPath = Path.Combine(_root, "settings.bin");
        var fileInfo = new FileInfo(settingsPath);
        var currentUser = WindowsIdentity.GetCurrent().User!;
        var denyReadRule = new FileSystemAccessRule(
            currentUser, FileSystemRights.Read | FileSystemRights.ReadData, AccessControlType.Deny);

        var acl = fileInfo.GetAccessControl();
        acl.AddAccessRule(denyReadRule);
        fileInfo.SetAccessControl(acl);
        try
        {
            // Sanity check: confirm this specific file state genuinely reproduces
            // UnauthorizedAccessException from the real File.ReadAllBytes call BEFORE trusting
            // LoadSettings' own handling of it -- a false-negative here (the deny rule silently
            // not taking effect) must fail loudly, never be mistaken for the fix working.
            var directReadException = Record.Exception(() => File.ReadAllBytes(settingsPath));
            Assert.IsType<UnauthorizedAccessException>(directReadException);

            var exception = Record.Exception(() => store.LoadSettings());

            Assert.Null(exception);
            var loaded = store.LoadSettings();
            Assert.Equal(ProtectionCategorySettings.AllOn, loaded.Categories);
        }
        finally
        {
            var restoredAcl = fileInfo.GetAccessControl();
            restoredAcl.RemoveAccessRule(denyReadRule);
            fileInfo.SetAccessControl(restoredAcl);
        }
    }

    // ==================================================================
    // CAT-010 -- Name/Address/Company settings persist correctly. NO behavioral/detector assertion
    // -- see class doc SCHEMA_SUPPORTED_DETECTOR_NOT_YET_IMPLEMENTED.
    // ==================================================================
    [Fact]
    public void Cat010_NameAddressCompany_PersistRoundTrip_SchemaOnly_NoDetectorClaim()
    {
        var store = OpenStore();
        var categories = new ProtectionCategorySettings(
            NameEnabled: false, PhoneEnabled: true, EmailEnabled: true, AddressEnabled: false, CompanyEnabled: false);
        store.SaveSettings(new PrivonSettings(ProtectionEnabled: true, Categories: categories));

        var loaded = store.LoadSettings();

        Assert.False(loaded.Categories!.NameEnabled);
        Assert.False(loaded.Categories.AddressEnabled);
        Assert.False(loaded.Categories.CompanyEnabled);
    }

    // ==================================================================
    // CAT-012 -- no raw PII path exists anywhere in the settings representation. Structural check:
    // ProtectionCategorySettings has exactly 5 boolean properties and nothing else -- it cannot
    // carry a string/PII value by construction, regardless of what any caller passes.
    // ==================================================================
    [Fact]
    public void Cat012_ProtectionCategorySettings_HasOnlyBooleanProperties_NoStringOrPiiField()
    {
        var properties = typeof(ProtectionCategorySettings).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        Assert.All(properties, p => Assert.Equal(typeof(bool), p.PropertyType));
        Assert.Equal(5, properties.Length);
    }

    [Fact]
    public void Cat012_SettingsFile_NeverContainsPlaintextJsonPropertyNames()
    {
        var store = OpenStore();
        store.SaveSettings(new PrivonSettings(ProtectionEnabled: true, Categories: ProtectionCategorySettings.AllOn));

        // The persisted file is AES-GCM ciphertext, never plaintext JSON -- guards against a
        // future accidental plaintext-serialization regression by confirming even a structural
        // property NAME (never mind a value) is not recoverable by inspecting the raw bytes.
        var rawFileBytes = File.ReadAllBytes(Path.Combine(_root, "settings.bin"));
        var propertyNameBytes = System.Text.Encoding.UTF8.GetBytes(nameof(ProtectionCategorySettings.NameEnabled));

        Assert.False(Matches(rawFileBytes, propertyNameBytes));
    }

    private static bool Matches(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return false;
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }
}
