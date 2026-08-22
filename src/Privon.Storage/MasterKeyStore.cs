using System.Security.Cryptography;

namespace Privon.Storage;

/// <summary>
/// Generates and persists the local random master key, protected via Windows DPAPI
/// (CurrentUser scope) so another Windows user account cannot decrypt it. If the stored
/// key cannot be loaded (missing or corrupted), a fresh key is generated and saved --
/// this alone never exposes or trusts old data: any payload previously encrypted under
/// the lost key will simply fail AES-GCM authentication under the new key and fall back
/// to safe defaults through the normal corruption path.
/// </summary>
public static class MasterKeyStore
{
    public const int KeySizeBytes = 32; // AES-256

    public static bool TryLoad(string path, out byte[] key)
    {
        key = [];
        if (!File.Exists(path)) return false;
        try
        {
            var protectedBytes = File.ReadAllBytes(path);
            key = System.Security.Cryptography.ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return key.Length == KeySizeBytes;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public static byte[] CreateAndSave(string path)
    {
        var key = new byte[KeySizeBytes];
        RandomNumberGenerator.Fill(key);
        var protectedBytes = System.Security.Cryptography.ProtectedData.Protect(key, optionalEntropy: null, DataProtectionScope.CurrentUser);
        AtomicFileWriter.WriteAllBytes(path, protectedBytes);
        return key;
    }

    public static byte[] LoadOrCreate(string path) => TryLoad(path, out var key) ? key : CreateAndSave(path);
}
