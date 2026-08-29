using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using Privon.App;
using Privon.Detection;
using Privon.Storage;

namespace Privon.App.Tests;

// PRIVON v0.2.1 Gate 3B -- UserExceptionProvider regression. Same technique as
// TrustExceptionProviderTests.cs/ProtectionCategorySettingsProviderTests.cs: a REAL
// PrivonLocalStore against a temp directory, no fake Storage seam. No mocking framework.
// Synthetic data only.
public class UserExceptionProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PrivonUserExceptionProviderTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private PrivonLocalStore OpenStore() => PrivonLocalStore.OpenOrCreate(_root);

    // ---- EXC-010 (provider-level) -- nothing saved -> empty ----
    [Fact]
    public void MissingStore_ReturnsEmptyList()
    {
        var store = OpenStore();

        Assert.Empty(new UserExceptionProvider(store).Load());
    }

    // ---- EXC-011 (provider-level) -- corrupted store -> empty ----
    [Fact]
    public void CorruptedStore_ReturnsEmptyList()
    {
        var path = Path.Combine(_root, "user-exceptions.bin");
        var store = OpenStore();
        store.SaveUserExceptions([new UserExceptionEntry("Phone", "01012345678")]);
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        Assert.Empty(new UserExceptionProvider(store).Load());
    }

    // ---- EXC-012 (provider-level) -- real unreadable/access-denied store -> empty ----
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
            Assert.IsType<UnauthorizedAccessException>(Record.Exception(() => File.ReadAllBytes(path)));
            Assert.Empty(new UserExceptionProvider(store).Load());
        }
        finally
        {
            var restoredAcl = fileInfo.GetAccessControl();
            restoredAcl.RemoveAccessRule(denyReadRule);
            fileInfo.SetAccessControl(restoredAcl);
        }
    }

    // ---- real mapping: one Phone entry -> one UserExceptionValue with correct PiiType/canonical ----
    [Fact]
    public void SinglePhoneEntry_MapsToOneValue_CorrectPiiTypeAndCanonical()
    {
        var store = OpenStore();
        store.SaveUserExceptions([new UserExceptionEntry("Phone", "01012345678")]);

        var loaded = new UserExceptionProvider(store).Load();

        var value = Assert.Single(loaded);
        Assert.Equal(PiiType.Phone, value.PiiType);
        Assert.Equal(new CanonicalValue(PiiType.Phone, "01012345678"), value.CanonicalValue);
    }

    // ---- unknown persisted PiiTypeId -> that entry is skipped, never a fabricated fallback
    // PiiType -- same discipline as TrustExceptionProvider's own TryMapCanonical ----
    [Fact]
    public void UnknownPiiTypeId_EntrySkipped_NeverFabricatesAFallbackPiiType()
    {
        var store = OpenStore();
        store.SaveUserExceptions(
        [
            new UserExceptionEntry("Phone", "01012345678"),
            new UserExceptionEntry("FutureType", "SYNTHETIC-UNKNOWN"),
        ]);

        var loaded = new UserExceptionProvider(store).Load();

        var value = Assert.Single(loaded);
        Assert.Equal(PiiType.Phone, value.PiiType);
    }

    [Fact]
    public void UnknownPiiTypeId_NeverThrows()
    {
        var store = OpenStore();
        store.SaveUserExceptions([new UserExceptionEntry("FutureType", "SYNTHETIC-UNKNOWN")]);

        var exception = Record.Exception(() => new UserExceptionProvider(store).Load());

        Assert.Null(exception);
    }

    // ---- fresh load per call, no cache ----
    [Fact]
    public void Load_NoCache_ReflectsSaveMadeBetweenCalls()
    {
        var store = OpenStore();
        var provider = new UserExceptionProvider(store);

        Assert.Empty(provider.Load());

        store.SaveUserExceptions([new UserExceptionEntry("Phone", "01012345678")]);
        var second = provider.Load();

        Assert.Single(second);
    }

    [Fact]
    public void Constructor_NullStore_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new UserExceptionProvider(null!));
    }

    // ---- structural boundary ----
    [Theory]
    [InlineData(typeof(IUserExceptionProvider))]
    [InlineData(typeof(UserExceptionProvider))]
    [InlineData(typeof(UserExceptionValue))]
    public void ProviderTypes_AreNotPublic(Type type)
    {
        Assert.False(type.IsPublic);
    }

    [Fact]
    public void UserExceptionProvider_HasNoSensitiveInstanceField()
    {
        var fields = typeof(UserExceptionProvider)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        var forbiddenTypes = new[]
        {
            typeof(string), typeof(CanonicalValue),
            typeof(IReadOnlyList<UserExceptionValue>), typeof(IReadOnlyList<UserExceptionEntry>),
            typeof(List<UserExceptionValue>),
        };

        Assert.DoesNotContain(fields, f => forbiddenTypes.Contains(f.FieldType));
        Assert.Contains(fields, f => f.FieldType == typeof(PrivonLocalStore));
    }

    // ---- diagnostic surface -- UserExceptionValue never exposes the canonical string ----
    [Fact]
    public void UserExceptionValue_ToString_DoesNotContainSentinel()
    {
        const string sentinel = "USER-EXCEPTION-VALUE-SENTINEL-410288";
        var value = new UserExceptionValue(PiiType.Phone, new CanonicalValue(PiiType.Phone, sentinel));

        Assert.DoesNotContain(sentinel, value.ToString());
        Assert.DoesNotContain(sentinel, $"{value}");
    }
}
