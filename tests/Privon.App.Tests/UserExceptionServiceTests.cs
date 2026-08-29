using Privon.App;
using Privon.Detection;
using Privon.Storage;

namespace Privon.App.Tests;

// PRIVON v0.2.1 Gate 3B -- UserExceptionService regression: the minimum mutation API
// (List/Add/Delete/Reset) the Settings UI needs. No in-memory mirror/cache -- List is load-only,
// Add/Delete are each a fresh load-modify-save directly against PrivonLocalStore (the same pattern
// SaveTrustedPublicInfo/SaveExceptions already establish for whole-list-replace persistence), and
// Reset saves an empty list directly without a prior load. Synthetic data only.
public class UserExceptionServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PrivonUserExceptionServiceTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private PrivonLocalStore OpenStore() => PrivonLocalStore.OpenOrCreate(_root);

    private static readonly CanonicalValue Phone1 = new(PiiType.Phone, "01012345678");
    private static readonly CanonicalValue Phone2 = new(PiiType.Phone, "01099998888");
    private static readonly CanonicalValue Email1 = new(PiiType.Email, "user@example.test");

    // ---- List: empty store -> empty list ----
    [Fact]
    public void List_EmptyStore_ReturnsEmpty()
    {
        var service = new UserExceptionService(OpenStore());

        Assert.Empty(service.List());
    }

    // ---- Add: identity = PiiType + canonical Value -> appears in List ----
    [Fact]
    public void Add_NewEntry_AppearsInList()
    {
        var service = new UserExceptionService(OpenStore());

        service.Add(PiiType.Phone, Phone1);

        var list = service.List();
        var entry = Assert.Single(list);
        Assert.Equal(PiiType.Phone, entry.PiiType);
        Assert.Equal(Phone1, entry.CanonicalValue);
    }

    // ==================================================================
    // EXC-009 -- duplicate add is deterministic/idempotent -- exactly one persisted entry.
    // ==================================================================
    [Fact]
    public void Exc009_DuplicateAdd_ExactlyOnePersistedEntry()
    {
        var service = new UserExceptionService(OpenStore());

        service.Add(PiiType.Phone, Phone1);
        service.Add(PiiType.Phone, Phone1);
        service.Add(PiiType.Phone, Phone1);

        Assert.Single(service.List());
    }

    // ---- Add with different PiiType/value stays distinct ----
    [Fact]
    public void Add_DifferentValues_AllPersisted()
    {
        var service = new UserExceptionService(OpenStore());

        service.Add(PiiType.Phone, Phone1);
        service.Add(PiiType.Phone, Phone2);
        service.Add(PiiType.Email, Email1);

        Assert.Equal(3, service.List().Count);
    }

    // ---- Delete: removes only the exact typed entry ----
    [Fact]
    public void Delete_ExactEntry_RemovedOnly()
    {
        var service = new UserExceptionService(OpenStore());
        service.Add(PiiType.Phone, Phone1);
        service.Add(PiiType.Phone, Phone2);

        service.Delete(PiiType.Phone, Phone1);

        var list = service.List();
        var remaining = Assert.Single(list);
        Assert.Equal(Phone2, remaining.CanonicalValue);
    }

    // ==================================================================
    // EXC-018 -- delete absent entry is deterministic/idempotent, no unrelated entry removed.
    // ==================================================================
    [Fact]
    public void Exc018_DeleteAbsentEntry_Idempotent_NoUnrelatedEntryRemoved()
    {
        var service = new UserExceptionService(OpenStore());
        service.Add(PiiType.Phone, Phone1);

        var exception = Record.Exception(() => service.Delete(PiiType.Phone, Phone2)); // never added

        Assert.Null(exception);
        var remaining = Assert.Single(service.List());
        Assert.Equal(Phone1, remaining.CanonicalValue);
    }

    [Fact]
    public void Exc018_DeleteFromEmptyStore_Idempotent_NoThrow()
    {
        var service = new UserExceptionService(OpenStore());

        var exception = Record.Exception(() => service.Delete(PiiType.Phone, Phone1));

        Assert.Null(exception);
        Assert.Empty(service.List());
    }

    // ---- Delete same PiiType different canonical value only removes the exact match, and same
    // canonical string under a DIFFERENT PiiType is untouched ----
    [Fact]
    public void Delete_SameStringDifferentPiiType_NeverRemoved()
    {
        var service = new UserExceptionService(OpenStore());
        var sameString = new CanonicalValue(PiiType.Phone, "SAME-STRING");
        var otherType = new CanonicalValue(PiiType.Email, "SAME-STRING");
        service.Add(PiiType.Phone, sameString);
        service.Add(PiiType.Email, otherType);

        service.Delete(PiiType.Phone, sameString);

        var remaining = Assert.Single(service.List());
        Assert.Equal(PiiType.Email, remaining.PiiType);
    }

    // ==================================================================
    // Reset -> empty list.
    // ==================================================================
    [Fact]
    public void Reset_EmptiesTheList()
    {
        var service = new UserExceptionService(OpenStore());
        service.Add(PiiType.Phone, Phone1);
        service.Add(PiiType.Email, Email1);

        service.Reset();

        Assert.Empty(service.List());
    }

    // ==================================================================
    // EXC-017 -- Add/Delete/Reset persistence round-trip: a fresh UserExceptionService instance
    // against the SAME underlying store observes exactly the persisted state, proving no
    // in-memory mirror is what's actually being read.
    // ==================================================================
    [Fact]
    public void Exc017_AddThenReopen_PersistsAcrossFreshServiceInstance()
    {
        var store = OpenStore();
        new UserExceptionService(store).Add(PiiType.Phone, Phone1);

        var reopened = new UserExceptionService(store).List();

        var entry = Assert.Single(reopened);
        Assert.Equal(Phone1, entry.CanonicalValue);
    }

    [Fact]
    public void Exc017_DeleteThenReopen_PersistsAcrossFreshServiceInstance()
    {
        var store = OpenStore();
        var first = new UserExceptionService(store);
        first.Add(PiiType.Phone, Phone1);
        first.Add(PiiType.Phone, Phone2);
        first.Delete(PiiType.Phone, Phone1);

        var reopened = new UserExceptionService(store).List();

        var entry = Assert.Single(reopened);
        Assert.Equal(Phone2, entry.CanonicalValue);
    }

    [Fact]
    public void Exc017_ResetThenReopen_PersistsAcrossFreshServiceInstance()
    {
        var store = OpenStore();
        var first = new UserExceptionService(store);
        first.Add(PiiType.Phone, Phone1);
        first.Reset();

        var reopened = new UserExceptionService(store).List();

        Assert.Empty(reopened);
    }

    [Fact]
    public void Constructor_NullStore_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new UserExceptionService(null!));
    }

    [Fact]
    public void UserExceptionService_IsNotPublic()
    {
        Assert.False(typeof(UserExceptionService).IsPublic);
    }

    // ---- privacy: List() results never expose the canonical value through diagnostics ----
    [Fact]
    public void ListedValues_ToString_NeverExposesSentinel()
    {
        const string sentinel = "USER-EXCEPTION-SERVICE-SENTINEL-773410";
        var service = new UserExceptionService(OpenStore());
        service.Add(PiiType.Phone, new CanonicalValue(PiiType.Phone, sentinel));

        var entry = Assert.Single(service.List());

        Assert.DoesNotContain(sentinel, entry.ToString());
        Assert.DoesNotContain(sentinel, $"{entry}");
    }
}
