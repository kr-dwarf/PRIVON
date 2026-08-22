using Privon.Storage;

namespace Privon.Storage.Tests;

public class CorruptionFallbackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PrivonStorageTests_" + Guid.NewGuid().ToString("N"));

    private const int CiphertextOffset = 1 + SecureEnvelope.NonceSize + SecureEnvelope.TagSize;
    private const int TagOffset = 1 + SecureEnvelope.NonceSize;

    // Test 8: ciphertext 변조 -> 인증 실패 -> safe fallback (settings default, never throws).
    [Fact]
    public void TamperedCiphertext_FallsBackToSafeDefault()
    {
        var settingsPath = Path.Combine(_root, "settings.bin");
        var store = PrivonLocalStore.OpenOrCreate(_root);
        store.SaveSettings(new PrivonSettings(ProtectionEnabled: false));

        FlipByte(settingsPath, CiphertextOffset);

        var loaded = store.LoadSettings();
        Assert.Equal(PrivonSettings.SafeDefault, loaded);
    }

    // Test 9: tag 변조 -> safe fallback.
    [Fact]
    public void TamperedTag_FallsBackToSafeDefault()
    {
        var settingsPath = Path.Combine(_root, "settings.bin");
        var store = PrivonLocalStore.OpenOrCreate(_root);
        store.SaveSettings(new PrivonSettings(ProtectionEnabled: false));

        FlipByte(settingsPath, TagOffset);

        var loaded = store.LoadSettings();
        Assert.Equal(PrivonSettings.SafeDefault, loaded);
    }

    // Test 10: 잘못된 formatVersion -> safe fallback.
    [Fact]
    public void UnknownFormatVersion_FallsBackToSafeDefault()
    {
        var settingsPath = Path.Combine(_root, "settings.bin");
        var store = PrivonLocalStore.OpenOrCreate(_root);
        store.SaveSettings(new PrivonSettings(ProtectionEnabled: false));

        var bytes = File.ReadAllBytes(settingsPath);
        bytes[0] = 99;
        File.WriteAllBytes(settingsPath, bytes);

        var loaded = store.LoadSettings();
        Assert.Equal(PrivonSettings.SafeDefault, loaded);
    }

    // Test 10 (trusted/exceptions safe defaults are empty, not "unknown trusted").
    [Fact]
    public void UnknownFormatVersion_TrustedStore_FallsBackToEmpty()
    {
        var trustedPath = Path.Combine(_root, "trusted-public-info.bin");
        var store = PrivonLocalStore.OpenOrCreate(_root);
        store.SaveTrustedPublicInfo([new TrustedPublicInfoEntry("Phone", "SYNTHETIC-TRUST-VALUE")]);

        var bytes = File.ReadAllBytes(trustedPath);
        bytes[0] = 77;
        File.WriteAllBytes(trustedPath, bytes);

        var loaded = store.LoadTrustedPublicInfo();
        Assert.Empty(loaded);
    }

    // Test 11: master-key 데이터 손상 -> safe fallback (old ciphertext becomes
    // undecryptable under the freshly regenerated key; must not throw or trust old data).
    [Fact]
    public void CorruptedMasterKey_FallsBackToSafeDefault()
    {
        var keyPath = Path.Combine(_root, "master.key");
        var store = PrivonLocalStore.OpenOrCreate(_root);
        store.SaveSettings(new PrivonSettings(ProtectionEnabled: false));

        FlipByte(keyPath, 0);

        var reopened = PrivonLocalStore.OpenOrCreate(_root);
        var loaded = reopened.LoadSettings();
        Assert.Equal(PrivonSettings.SafeDefault, loaded);
    }

    private static void FlipByte(string path, int offset)
    {
        var bytes = File.ReadAllBytes(path);
        bytes[offset] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
