using System.Security.AccessControl;
using System.Security.Principal;
using Privon.App;
using Privon.Storage;

namespace Privon.App.Tests;

// PRIVON v0.2.1 Gate 3C -- ProtectionCategorySettingsService regression: Load/SetPhoneEnabled/
// SetEmailEnabled/Reset, PRESERVE_UNRELATED_STATE, and WRITE_FAILURE_POLICY (UI-006/UI-024/UI-025).
// Same technique as ProtectionCategorySettingsProviderTests/UserExceptionServiceTests: a REAL
// PrivonLocalStore against a temp directory, no fake Storage seam. Synthetic data only.
public class ProtectionCategorySettingsServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PrivonCategoryServiceTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private PrivonLocalStore OpenStore() => PrivonLocalStore.OpenOrCreate(_root);

    [Fact]
    public void Load_NothingPersisted_ReturnsAllOn()
    {
        var service = new ProtectionCategorySettingsService(OpenStore());

        Assert.Equal(ProtectionCategorySettings.AllOn, service.Load());
    }

    [Fact]
    public void SetPhoneEnabled_False_PersistsAndReloads()
    {
        var service = new ProtectionCategorySettingsService(OpenStore());

        service.SetPhoneEnabled(false);

        Assert.False(service.Load().PhoneEnabled);
    }

    [Fact]
    public void SetPhoneEnabled_True_AfterFalse_ReEnables()
    {
        var service = new ProtectionCategorySettingsService(OpenStore());
        service.SetPhoneEnabled(false);

        service.SetPhoneEnabled(true);

        Assert.True(service.Load().PhoneEnabled);
    }

    [Fact]
    public void SetEmailEnabled_False_PersistsAndReloads()
    {
        var service = new ProtectionCategorySettingsService(OpenStore());

        service.SetEmailEnabled(false);

        Assert.False(service.Load().EmailEnabled);
    }

    // ==================================================================
    // UI-024 -- mutation preserves unrelated PrivonSettings fields/categories.
    // ==================================================================
    [Fact]
    public void SetPhoneEnabled_PreservesEmailAndOtherCategoryFields()
    {
        var store = OpenStore();
        store.SaveSettings(new PrivonSettings(
            ProtectionEnabled: true,
            Categories: new ProtectionCategorySettings(NameEnabled: false, PhoneEnabled: true, EmailEnabled: false, AddressEnabled: false, CompanyEnabled: true)));
        var service = new ProtectionCategorySettingsService(store);

        service.SetPhoneEnabled(false);

        var loaded = service.Load();
        Assert.False(loaded.PhoneEnabled);
        Assert.False(loaded.EmailEnabled); // untouched
        Assert.False(loaded.NameEnabled); // untouched
        Assert.False(loaded.AddressEnabled); // untouched
        Assert.True(loaded.CompanyEnabled); // untouched
    }

    [Fact]
    public void SetEmailEnabled_PreservesProtectionEnabledAndPausedUntil()
    {
        var store = OpenStore();
        var pausedUntil = DateTimeOffset.UtcNow.AddHours(1);
        store.SaveSettings(new PrivonSettings(ProtectionEnabled: false, PausedUntilUtc: pausedUntil, Categories: ProtectionCategorySettings.AllOn));
        var service = new ProtectionCategorySettingsService(store);

        service.SetEmailEnabled(false);

        var raw = store.LoadSettings();
        Assert.False(raw.ProtectionEnabled);
        Assert.Equal(pausedUntil, raw.PausedUntilUtc);
    }

    // ==================================================================
    // UI-006 -- Reset -> Categories == AllOn, other PrivonSettings state preserved.
    // ==================================================================
    [Fact]
    public void Reset_RestoresAllOn_PreservesProtectionEnabled()
    {
        var store = OpenStore();
        store.SaveSettings(new PrivonSettings(
            ProtectionEnabled: false,
            Categories: new ProtectionCategorySettings(false, false, false, false, false)));
        var service = new ProtectionCategorySettingsService(store);

        service.Reset();

        Assert.Equal(ProtectionCategorySettings.AllOn, service.Load());
        Assert.False(store.LoadSettings().ProtectionEnabled); // untouched by Reset
    }

    [Fact]
    public void Constructor_NullStore_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ProtectionCategorySettingsService(null!));
    }

    [Fact]
    public void ProtectionCategorySettingsService_IsNotPublic()
    {
        Assert.False(typeof(ProtectionCategorySettingsService).IsPublic);
    }

    // ==================================================================
    // UI-025 -- failed Save propagates; existing on-disk state is never silently claimed as
    // persisted. Uses the same proven real Windows ACL Deny technique as MasterKeyFailSafeTests.
    // ==================================================================
    [Fact]
    public void SetPhoneEnabled_MasterKeyUnavailable_ThrowsAndDoesNotPersist()
    {
        var keyPath = Path.Combine(_root, "master.key");
        var settingsPath = Path.Combine(_root, "settings.bin");
        var setup = OpenStore();
        setup.SaveSettings(new PrivonSettings(ProtectionEnabled: true, Categories: ProtectionCategorySettings.AllOn));
        var bytesBefore = File.ReadAllBytes(settingsPath);

        using (DenyRead(keyPath))
        {
            var store = OpenStore(); // fresh instance -- observes the denied key as MASTER_KEY_UNAVAILABLE
            var service = new ProtectionCategorySettingsService(store);

            var exception = Record.Exception(() => service.SetPhoneEnabled(false));

            Assert.NotNull(exception);
            Assert.Equal(bytesBefore, File.ReadAllBytes(settingsPath)); // never persisted under a replacement key
            // Reload after the failed attempt still resolves to the safe default -- never a false
            // "persisted" claim, and never the stale on-disk value either (both are equally correct
            // here since AllOn was already what was on disk, but the important invariant is that the
            // rejected `false` never appears).
            Assert.True(service.Load().PhoneEnabled);
        }
    }

    private static IDisposable DenyRead(string path)
    {
        var fileInfo = new FileInfo(path);
        var currentUser = WindowsIdentity.GetCurrent().User!;
        var rule = new FileSystemAccessRule(currentUser, FileSystemRights.Read | FileSystemRights.ReadData, AccessControlType.Deny);
        var acl = fileInfo.GetAccessControl();
        acl.AddAccessRule(rule);
        fileInfo.SetAccessControl(acl);

        var directReadException = Record.Exception(() => File.ReadAllBytes(path));
        if (directReadException is not UnauthorizedAccessException)
        {
            var restoredAcl = fileInfo.GetAccessControl();
            restoredAcl.RemoveAccessRule(rule);
            fileInfo.SetAccessControl(restoredAcl);
            throw new InvalidOperationException(
                $"ACL deny rule did not reproduce UnauthorizedAccessException on {path} -- harness is wrong, not the fix under test.");
        }

        return new AclRestorer(fileInfo, rule);
    }

    private sealed class AclRestorer(FileInfo fileInfo, FileSystemAccessRule rule) : IDisposable
    {
        public void Dispose()
        {
            var acl = fileInfo.GetAccessControl();
            acl.RemoveAccessRule(rule);
            fileInfo.SetAccessControl(acl);
        }
    }
}
