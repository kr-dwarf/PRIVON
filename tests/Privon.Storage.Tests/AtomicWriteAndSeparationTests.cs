using System.Text;
using Privon.Storage;

namespace Privon.Storage.Tests;

public class AtomicWriteAndSeparationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PrivonStorageTests_" + Guid.NewGuid().ToString("N"));

    // Test 12: atomic write 중 실패를 시뮬레이션해 기존 파일 보존.
    [Fact]
    public void FailureDuringWrite_PreservesExistingFile()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "target.bin");
        var original = Encoding.UTF8.GetBytes("original-safe-content");
        AtomicFileWriter.WriteAllBytes(path, original);

        Assert.Throws<InvalidOperationException>(() =>
            AtomicFileWriter.WriteAtomic(path, stream =>
            {
                stream.Write(Encoding.UTF8.GetBytes("partial-new-content"));
                throw new InvalidOperationException("simulated mid-write failure");
            }));

        var afterFailure = File.ReadAllBytes(path);
        Assert.Equal(original, afterFailure);

        // No leftover temp file in the directory.
        var leftoverTempFiles = Directory.GetFiles(_root, "*.tmp");
        Assert.Empty(leftoverTempFiles);
    }

    // Test 13: 저장 파일에 synthetic 원문이 평문으로 존재하지 않음.
    [Fact]
    public void SavedFile_DoesNotContainPlaintextValue()
    {
        var store = PrivonLocalStore.OpenOrCreate(_root);
        const string marker = "SYNTHETIC-PLAINTEXT-MARKER-9F3A";
        store.SaveTrustedPublicInfo([new TrustedPublicInfoEntry("Phone", marker)]);

        var rawFileBytes = File.ReadAllBytes(Path.Combine(_root, "trusted-public-info.bin"));
        var markerBytes = Encoding.UTF8.GetBytes(marker);

        Assert.False(Matches(rawFileBytes, markerBytes));
    }

    // Test 14: 서로 다른 purpose/store 간 payload 교차 복호화가 되지 않도록 분리.
    [Fact]
    public void CrossStoreDecryption_Fails()
    {
        var store = PrivonLocalStore.OpenOrCreate(_root);
        store.SaveSettings(new PrivonSettings(ProtectionEnabled: false));

        var settingsPath = Path.Combine(_root, "settings.bin");
        var settingsBytes = File.ReadAllBytes(settingsPath);
        var envelope = SecureEnvelope.Decode(settingsBytes);

        // Same key, same envelope, but decrypted under a different store's purpose/AAD.
        var keyPath = Path.Combine(_root, "master.key");
        var masterKey = MasterKeyStore.LoadOrCreate(keyPath);

        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() =>
            SecureEnvelopeCodec.Decrypt(masterKey, StorePurpose.TrustedPublicInfo, envelope));
    }

    private static bool Matches(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return false;
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
