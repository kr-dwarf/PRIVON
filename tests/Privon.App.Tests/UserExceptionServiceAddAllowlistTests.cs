using Privon.App;
using Privon.Detection;
using Privon.Storage;

namespace Privon.App.Tests;

// PRIVON v0.2.1 Gate 3C -- UI-013/UI-026: UserExceptionService.Add's own defense-in-depth
// ADD_ALLOWLIST guard (see its own doc). Proves rejection happens BEFORE any persistence -- never
// merely that Add "throws" -- and that Phone/Email remain fully unaffected. Real PrivonLocalStore
// against a temp directory, no fake Storage seam, synthetic data only.
public class UserExceptionServiceAddAllowlistTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PrivonUserExceptionAllowlistTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private PrivonLocalStore OpenStore() => PrivonLocalStore.OpenOrCreate(_root);

    public static IEnumerable<object[]> RejectedTypes =>
    [
        [PiiType.ResidentRegistrationNumber, "9001011234567"],
        [PiiType.CardNumber, "4111111111111111"],
        [PiiType.BankAccountNumber, "110-234-567890"],
        [PiiType.Secret, "sk-synthetic-secret-value"],
        [PiiType.IpAddress, "192.168.0.1"],
        [PiiType.MacAddress, "00:11:22:33:44:55"],
        [PiiType.GpsCoordinate, "37.5,127.0"],
    ];

    // ---- UI-013 / UI-026 -- Level3 and unsupported-product-scope types are rejected before any
    // write; the store remains untouched. ----
    [Theory]
    [MemberData(nameof(RejectedTypes))]
    public void Add_RejectedType_ThrowsBeforePersistence_StoreUntouched(PiiType piiType, string value)
    {
        var store = OpenStore();
        var service = new UserExceptionService(store);

        Assert.Throws<ArgumentException>(() => service.Add(piiType, new CanonicalValue(piiType, value)));

        Assert.Empty(service.List());
        Assert.Empty(store.LoadUserExceptions());
    }

    // ---- Phone/Email remain fully addable -- the allowlist guard never affects the approved types. ----
    [Fact]
    public void Add_Phone_StillSucceeds()
    {
        var service = new UserExceptionService(OpenStore());
        var value = new CanonicalValue(PiiType.Phone, "01011112222");

        service.Add(PiiType.Phone, value);

        var entry = Assert.Single(service.List());
        Assert.Equal(PiiType.Phone, entry.PiiType);
        Assert.Equal(value, entry.CanonicalValue);
    }

    [Fact]
    public void Add_Email_StillSucceeds()
    {
        var service = new UserExceptionService(OpenStore());
        var value = new CanonicalValue(PiiType.Email, "user@example.test");

        service.Add(PiiType.Email, value);

        var entry = Assert.Single(service.List());
        Assert.Equal(PiiType.Email, entry.PiiType);
        Assert.Equal(value, entry.CanonicalValue);
    }

    // ---- A rejected Add never disturbs entries already persisted for OTHER (approved) types. ----
    [Fact]
    public void Add_RejectedType_DoesNotDisturbExistingApprovedEntries()
    {
        var service = new UserExceptionService(OpenStore());
        service.Add(PiiType.Phone, new CanonicalValue(PiiType.Phone, "01011112222"));

        Assert.Throws<ArgumentException>(() =>
            service.Add(PiiType.Secret, new CanonicalValue(PiiType.Secret, "sk-synthetic")));

        var remaining = Assert.Single(service.List());
        Assert.Equal(PiiType.Phone, remaining.PiiType);
    }

    // ---- Delete/Reset remain untouched by this guard -- it applies to Add only. ----
    [Fact]
    public void Delete_RejectedType_StillWorks_GuardIsAddOnly()
    {
        var store = OpenStore();
        // Seed a Level3 entry directly at the Storage layer (bypassing Add's own guard) -- proves
        // Delete/Reset remain able to clean up any legacy/out-of-band entry, even though Add can no
        // longer create a new one.
        store.SaveUserExceptions([new UserExceptionEntry("Secret", "sk-legacy-synthetic")]);
        var service = new UserExceptionService(store);
        Assert.Single(service.List());

        var exception = Record.Exception(() =>
            service.Delete(PiiType.Secret, new CanonicalValue(PiiType.Secret, "sk-legacy-synthetic")));

        Assert.Null(exception);
        Assert.Empty(service.List());
    }
}
