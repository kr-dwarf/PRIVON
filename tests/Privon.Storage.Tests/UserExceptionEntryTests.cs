using System.Security.AccessControl;
using System.Security.Principal;

namespace Privon.Storage.Tests;

// PRIVON v0.2.1 Gate 3B -- UserExceptionEntry persistence regression. Genuinely separate
// persisted structure from ExceptionEntry/TrustedPublicInfoEntry -- own file (user-exceptions.bin),
// own StorePurpose (distinct AAD binding), same encrypted-envelope/atomic-save/LoadOrDefault
// fail-safe infrastructure reused verbatim. Synthetic data only.
public class UserExceptionEntryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PrivonUserExceptionStorageTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private PrivonLocalStore OpenStore() => PrivonLocalStore.OpenOrCreate(_root);

    // ---- distinct file, never overloads exceptions.bin/trusted-public-info.bin/settings.bin ----
    [Fact]
    public void SaveUserExceptions_WritesToItsOwnDistinctFile()
    {
        var store = OpenStore();
        store.SaveUserExceptions([new UserExceptionEntry("Phone", "01012345678")]);

        Assert.True(File.Exists(Path.Combine(_root, "user-exceptions.bin")));
        Assert.False(File.Exists(Path.Combine(_root, "exceptions.bin")));
        Assert.False(File.Exists(Path.Combine(_root, "trusted-public-info.bin")));
        Assert.False(File.Exists(Path.Combine(_root, "settings.bin")));
    }

    // ---- distinct StorePurpose -- cross-purpose decryption fails, same technique as
    // AtomicWriteAndSeparationTests.CrossStoreDecryption_Fails ----
    [Fact]
    public void UserExceptionsStorePurpose_IsDistinctFromEveryOtherStore()
    {
        Assert.NotEqual(StorePurpose.UserExceptions, StorePurpose.Settings);
        Assert.NotEqual(StorePurpose.UserExceptions, StorePurpose.Exceptions);
        Assert.NotEqual(StorePurpose.UserExceptions, StorePurpose.TrustedPublicInfo);
    }

    [Fact]
    public void CrossStoreDecryption_UserExceptionsPurpose_FailsUnderWrongPurpose()
    {
        var store = OpenStore();
        store.SaveUserExceptions([new UserExceptionEntry("Phone", "01012345678")]);

        var path = Path.Combine(_root, "user-exceptions.bin");
        var envelope = SecureEnvelope.Decode(File.ReadAllBytes(path));
        var masterKey = MasterKeyStore.LoadOrCreate(Path.Combine(_root, "master.key"));

        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() =>
            SecureEnvelopeCodec.Decrypt(masterKey, StorePurpose.Settings, envelope));
    }

    // ---- round-trip ----
    [Fact]
    public void RoundTrip_PreservesEntriesExactly()
    {
        var store = OpenStore();
        var entries = new List<UserExceptionEntry>
        {
            new("Phone", "01012345678"),
            new("Email", "user@example.test"),
        };
        store.SaveUserExceptions(entries);

        var loaded = store.LoadUserExceptions();

        Assert.Equal(entries, loaded);
    }

    // ---- EXC-010 (storage-level) -- missing store -> empty list ----
    [Fact]
    public void MissingStore_ReturnsEmptyList()
    {
        var store = OpenStore();

        Assert.Empty(store.LoadUserExceptions());
    }

    // ---- EXC-011 (storage-level) -- corrupt/tampered store -> empty list ----
    [Fact]
    public void CorruptStore_ReturnsEmptyList()
    {
        var path = Path.Combine(_root, "user-exceptions.bin");
        var store = OpenStore();
        store.SaveUserExceptions([new UserExceptionEntry("Phone", "01012345678")]);

        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        Assert.Empty(store.LoadUserExceptions());
    }

    // ---- EXC-012 (storage-level) -- real unreadable/access-denied store -> empty list. Same
    // proven real Windows ACL Deny technique as Gate 3A's CAT-013 -- current-user-only, own temp
    // directory, no admin rights, ACL restored in finally regardless of outcome. ----
    [Fact]
    public void UnreadableStore_ReturnsEmptyList()
    {
        var store = OpenStore();
        store.SaveUserExceptions([new UserExceptionEntry("Phone", "01012345678")]);

        var path = Path.Combine(_root, "user-exceptions.bin");
        var fileInfo = new FileInfo(path);
        var currentUser = WindowsIdentity.GetCurrent().User!;
        var denyReadRule = new FileSystemAccessRule(
            currentUser, FileSystemRights.Read | FileSystemRights.ReadData, AccessControlType.Deny);

        var acl = fileInfo.GetAccessControl();
        acl.AddAccessRule(denyReadRule);
        fileInfo.SetAccessControl(acl);
        try
        {
            var directReadException = Record.Exception(() => File.ReadAllBytes(path));
            Assert.IsType<UnauthorizedAccessException>(directReadException);

            var exception = Record.Exception(() => store.LoadUserExceptions());
            Assert.Null(exception);
            Assert.Empty(store.LoadUserExceptions());
        }
        finally
        {
            var restoredAcl = fileInfo.GetAccessControl();
            restoredAcl.RemoveAccessRule(denyReadRule);
            fileInfo.SetAccessControl(restoredAcl);
        }
    }

    // ---- malformed entry (empty PiiTypeId/Value) makes the WHOLE store untrustworthy on load,
    // same ValidateStructure discipline already established for ExceptionEntry/
    // TrustedPublicInfoEntry ----
    [Fact]
    public void SaveMalformedEntry_EmptyValue_Throws()
    {
        var store = OpenStore();

        Assert.Throws<ArgumentException>(() => store.SaveUserExceptions([new UserExceptionEntry("Phone", "")]));
    }

    // ---- EXC-016 (storage-level) -- ToString never exposes Value ----
    [Fact]
    public void ToString_NeverExposesValue()
    {
        const string sentinel = "RAW-USER-EXCEPTION-SENTINEL-583920";
        var entry = new UserExceptionEntry("Phone", sentinel);

        Assert.DoesNotContain(sentinel, entry.ToString());
        Assert.DoesNotContain(sentinel, $"{entry}");
        Assert.Contains("PiiTypeId", entry.ToString());
    }

    [Fact]
    public void SettingsFile_NeverContainsPlaintextValue()
    {
        const string sentinel = "RAW-USER-EXCEPTION-SENTINEL-583920";
        var store = OpenStore();
        store.SaveUserExceptions([new UserExceptionEntry("Phone", sentinel)]);

        var rawFileBytes = File.ReadAllBytes(Path.Combine(_root, "user-exceptions.bin"));
        var sentinelBytes = System.Text.Encoding.UTF8.GetBytes(sentinel);

        Assert.False(Matches(rawFileBytes, sentinelBytes));
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
