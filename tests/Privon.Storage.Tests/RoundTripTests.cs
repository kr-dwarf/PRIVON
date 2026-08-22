using Privon.Storage;

namespace Privon.Storage.Tests;

public class RoundTripTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PrivonStorageTests_" + Guid.NewGuid().ToString("N"));

    // Test 5: settings encrypt -> decrypt roundtrip.
    [Fact]
    public void Settings_RoundTrips()
    {
        var store = PrivonLocalStore.OpenOrCreate(_root);
        var settings = new PrivonSettings(ProtectionEnabled: false, PausedUntilUtc: DateTimeOffset.UtcNow);

        store.SaveSettings(settings);
        var loaded = store.LoadSettings();

        Assert.Equal(settings, loaded);
    }

    // Test 6: trusted store encrypt -> decrypt roundtrip.
    [Fact]
    public void TrustedPublicInfo_RoundTrips()
    {
        var store = PrivonLocalStore.OpenOrCreate(_root);
        var entries = new List<TrustedPublicInfoEntry> { new("Phone", "SYNTHETIC-TRUST-VALUE-001"), new("Email", "SYNTHETIC-TRUST-VALUE-002") };

        store.SaveTrustedPublicInfo(entries);
        var loaded = store.LoadTrustedPublicInfo();

        Assert.Equal(entries, loaded);
    }

    // Test 7: exception store encrypt -> decrypt roundtrip.
    [Fact]
    public void Exceptions_RoundTrip()
    {
        var store = PrivonLocalStore.OpenOrCreate(_root);
        var entries = new List<ExceptionEntry> { new("Phone", "SYNTHETIC-EXCEPTION-VALUE-001") };

        store.SaveExceptions(entries);
        var loaded = store.LoadExceptions();

        Assert.Equal(entries, loaded);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
