using Privon.App;

namespace Privon.App.Tests;

// Phase 0.2I -- SingleInstanceGuard regression. Uses a REAL System.Threading.Mutex under a
// randomized, per-test unique name (GUID-suffixed) -- deterministic and side-effect-free (a named
// Mutex under a random test-only name can never collide with the real production
// "PRIVON-SingleInstance-..." name App.xaml.cs uses, and is automatically cleaned up by the OS once
// every handle referencing it is disposed) -- exactly matching this codebase's established
// precedent of exercising real Windows primitives directly wherever it is safe to do so (e.g.
// Win32Source_TryGetProcessName_NonexistentPid_ReturnsFalse_DoesNotThrow).
public class SingleInstanceGuardTests
{
    private static string UniqueName([System.Runtime.CompilerServices.CallerMemberName] string caller = "") =>
        $"PRIVON-Test-{caller}-{Guid.NewGuid():N}";

    // ---- H. SINGLE INSTANCE ----
    [Fact]
    public void TryAcquire_FirstInstance_Succeeds()
    {
        var name = UniqueName();
        using var guard = new SingleInstanceGuard(name);

        Assert.True(guard.TryAcquire());
    }

    [Fact]
    public void TryAcquire_SecondInstance_SameName_Fails_NeverBlocks()
    {
        var name = UniqueName();
        using var first = new SingleInstanceGuard(name);
        Assert.True(first.TryAcquire());

        // A named Mutex's ownership is THREAD-scoped and recursive for the SAME thread (a real
        // production "second instance," being a genuinely separate process, is always a separate
        // thread too) -- so the contended attempt is deliberately made from a real second Thread,
        // never merely a second SingleInstanceGuard object on this same calling thread (which would
        // trivially "succeed" via same-thread mutex recursion and prove nothing about real
        // duplicate-instance contention).
        bool? secondAcquired = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var secondThread = new Thread(() =>
        {
            using var second = new SingleInstanceGuard(name);
            secondAcquired = second.TryAcquire();
        });
        secondThread.Start();
        secondThread.Join();
        sw.Stop();

        Assert.False(secondAcquired);
        // Non-blocking -- a genuinely blocking WaitOne would hang here indefinitely (first never
        // releases within this test), so any reasonably fast return proves TimeSpan.Zero semantics.
        Assert.True(sw.ElapsedMilliseconds < 2000, $"TryAcquire took {sw.ElapsedMilliseconds}ms -- expected a non-blocking, near-instant failure.");
    }

    [Fact]
    public void Dispose_ReleasesOwnership_LaterInstanceCanThenAcquire()
    {
        var name = UniqueName();
        var first = new SingleInstanceGuard(name);
        Assert.True(first.TryAcquire());

        first.Dispose();

        using var second = new SingleInstanceGuard(name);
        Assert.True(second.TryAcquire());
    }

    [Fact]
    public void TryAcquire_AfterAbandonment_StillSucceeds_NoPermanentLockout()
    {
        var name = UniqueName();

        // A separate real OS thread acquires the SAME-named mutex and then exits WITHOUT ever
        // releasing it -- the OS marks the mutex "abandoned." Thread.Join guarantees this has
        // already happened (deterministically, no sleep/poll) before this test proceeds.
        var abandonedThread = new Thread(() =>
        {
            var m = new Mutex(initiallyOwned: false, name);
            m.WaitOne();
            // Deliberately never calls m.ReleaseMutex() -- simulates a crashed prior instance.
        });
        abandonedThread.Start();
        abandonedThread.Join();

        using var guard = new SingleInstanceGuard(name);
        bool acquired = guard.TryAcquire();

        Assert.True(acquired); // must NOT be permanently locked out by the abandoned owner
    }

    [Fact]
    public void TryAcquire_CalledTwiceOnSameInstance_StaysTrue_DoesNotThrow()
    {
        var name = UniqueName();
        using var guard = new SingleInstanceGuard(name);

        Assert.True(guard.TryAcquire());
        Assert.True(guard.TryAcquire());
    }

    [Fact]
    public void Dispose_SafeWhenTryAcquireNeverCalled()
    {
        var name = UniqueName();
        var guard = new SingleInstanceGuard(name);

        guard.Dispose(); // must not throw
    }

    [Fact]
    public void Dispose_SafeWhenAcquisitionFailed()
    {
        var name = UniqueName();
        using var first = new SingleInstanceGuard(name);
        Assert.True(first.TryAcquire());

        // Same cross-thread reasoning as TryAcquire_SecondInstance_SameName_Fails_NeverBlocks --
        // the failed TryAcquire() call itself must run on a genuinely different thread from the
        // one that already owns the mutex, or same-thread recursion would let it "succeed" and
        // this test would no longer be exercising the failure path it claims to.
        var second = new SingleInstanceGuard(name);
        bool? secondAcquired = null;
        var secondThread = new Thread(() => secondAcquired = second.TryAcquire());
        secondThread.Start();
        secondThread.Join();

        Assert.False(secondAcquired);

        second.Dispose(); // must not throw even though this instance never actually owned it
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var name = UniqueName();
        var guard = new SingleInstanceGuard(name);
        guard.TryAcquire();

        guard.Dispose();
        guard.Dispose();
    }

    [Fact]
    public void TryAcquire_AfterDispose_Throws()
    {
        var name = UniqueName();
        var guard = new SingleInstanceGuard(name);
        guard.Dispose();

        Assert.Throws<ObjectDisposedException>(() => guard.TryAcquire());
    }

    [Fact]
    public void Constructor_NullName_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SingleInstanceGuard(null!));
    }

    [Fact]
    public void Constructor_EmptyName_Throws()
    {
        Assert.Throws<ArgumentException>(() => new SingleInstanceGuard(""));
    }

    [Fact]
    public void TypeIsNotPublic()
    {
        Assert.False(typeof(SingleInstanceGuard).IsPublic);
    }
}
