using System.Reflection;
using Privon.App;
using Privon.Storage;

namespace Privon.App.Tests;

// PRIVON v0.2.1 Gate 3A -- ProtectionCategorySettingsProvider regression. Same technique as
// TrustExceptionProviderTests.cs: a REAL PrivonLocalStore against a temp directory, no fake
// Storage seam. No mocking framework. Synthetic data only.
public class ProtectionCategorySettingsProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PrivonCategoryProviderTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private PrivonLocalStore OpenStore() => PrivonLocalStore.OpenOrCreate(_root);

    // ---- CAT-001 (provider-level companion) -- nothing ever saved -> AllOn ----
    [Fact]
    public void MissingSettings_ReturnsAllOn()
    {
        var store = OpenStore();

        var loaded = new ProtectionCategorySettingsProvider(store).Load();

        Assert.Equal(ProtectionCategorySettings.AllOn, loaded);
    }

    // ---- CAT-002 (provider-level companion) -- round-trips a genuinely mixed pattern ----
    [Fact]
    public void PersistedMixedSettings_LoadsExactly()
    {
        var store = OpenStore();
        var categories = new ProtectionCategorySettings(
            NameEnabled: true, PhoneEnabled: false, EmailEnabled: true, AddressEnabled: false, CompanyEnabled: true);
        store.SaveSettings(new PrivonSettings(ProtectionEnabled: true, Categories: categories));

        var loaded = new ProtectionCategorySettingsProvider(store).Load();

        Assert.Equal(categories, loaded);
    }

    // ---- CAT-003 (provider-level companion) -- corrupted settings -> AllOn ----
    [Fact]
    public void CorruptedSettings_ReturnsAllOn()
    {
        var settingsPath = Path.Combine(_root, "settings.bin");
        var store = OpenStore();
        store.SaveSettings(new PrivonSettings(
            ProtectionEnabled: true, Categories: new ProtectionCategorySettings(false, false, false, false, false)));
        var bytes = File.ReadAllBytes(settingsPath);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(settingsPath, bytes);

        var loaded = new ProtectionCategorySettingsProvider(store).Load();

        Assert.Equal(ProtectionCategorySettings.AllOn, loaded);
    }

    // ---- fresh load per call, no cache -- a save between two Load() calls on the SAME provider
    // instance is observed on the second call ----
    [Fact]
    public void Load_NoCache_ReflectsSaveMadeBetweenCalls()
    {
        var store = OpenStore();
        var provider = new ProtectionCategorySettingsProvider(store);

        var first = provider.Load();
        Assert.Equal(ProtectionCategorySettings.AllOn, first);

        store.SaveSettings(new PrivonSettings(ProtectionEnabled: true, Categories: ProtectionCategorySettings.AllOn with { PhoneEnabled = false }));
        var second = provider.Load();

        Assert.False(second.PhoneEnabled);
    }

    [Fact]
    public void Constructor_NullStore_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ProtectionCategorySettingsProvider(null!));
    }

    // ---- structural boundary -- App-internal orchestration type, same accessibility discipline
    // as ITrustExceptionProvider/TrustExceptionProvider ----
    [Theory]
    [InlineData(typeof(IProtectionCategorySettingsProvider))]
    [InlineData(typeof(ProtectionCategorySettingsProvider))]
    public void ProviderTypes_AreNotPublic(Type type)
    {
        Assert.False(type.IsPublic);
    }

    // ---- the provider retains no sensitive/settings-shaped instance field -- the only
    // persistent field is the injected PrivonLocalStore itself ----
    [Fact]
    public void ProtectionCategorySettingsProvider_HasNoSettingsShapedInstanceField()
    {
        var fields = typeof(ProtectionCategorySettingsProvider)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.DoesNotContain(fields, f => f.FieldType == typeof(ProtectionCategorySettings));
        Assert.DoesNotContain(fields, f => f.FieldType == typeof(PrivonSettings));
        Assert.Contains(fields, f => f.FieldType == typeof(PrivonLocalStore));
    }
}
