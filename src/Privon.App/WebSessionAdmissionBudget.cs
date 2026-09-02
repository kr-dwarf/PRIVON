namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate 031F6I (E3) -- the explicit resource-safety capacity for concurrently admitted
/// Web channel sessions (Gate 031F6H R33), wrapping a private <see cref="SemaphoreSlim"/>. The OS
/// named-pipe server's own <c>MaxAllowedServerInstances</c> setting is never used as product flow
/// control -- this budget is the one and only admission gate. <see cref="AcquireAsync"/> returns a
/// <see cref="Lease"/> only once a permit has genuinely been acquired; a cancelled wait grants no
/// Lease and leaves the available count unchanged (standard <see cref="SemaphoreSlim.WaitAsync(System.Threading.CancellationToken)"/>
/// semantics: cancellation never consumes a permit).
/// </summary>
internal sealed class WebSessionAdmissionBudget : IDisposable
{
    private readonly SemaphoreSlim _semaphore;

    public WebSessionAdmissionBudget(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "capacity must be positive.");

        _semaphore = new SemaphoreSlim(capacity, capacity);
    }

    public async Task<Lease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(this);
    }

    private void Release() => _semaphore.Release();

    /// <summary>Gate 031F6H section 22/23: disposed LAST in the shutdown barrier, and only once the
    /// accept loop has provably stopped admitting new sessions. Not part of the frozen 031F6H test
    /// surface (additive) -- no RED test constructs a budget expecting it to be non-disposable.</summary>
    public void Dispose() => _semaphore.Dispose();

    /// <summary>The owned, idempotent admission permit. <see cref="Dispose"/> returns exactly one
    /// permit to the owning budget, no matter how many times it is called (Interlocked
    /// exactly-once release -- Gate 031F6H R33-D: a double Dispose must never over-release).</summary>
    public sealed class Lease : IDisposable
    {
        private WebSessionAdmissionBudget? _budget;

        internal Lease(WebSessionAdmissionBudget budget)
        {
            _budget = budget;
        }

        public void Dispose()
        {
            var budget = Interlocked.Exchange(ref _budget, null);
            try { budget?.Release(); }
            catch (ObjectDisposedException)
            {
                // The owning budget was itself disposed (Gate 031F6H section 22/23 shutdown barrel,
                // step 9) while this Lease's own session was still in its own bounded teardown --
                // Close() must still never throw (Gate 031F6H section 19).
            }
        }
    }
}
