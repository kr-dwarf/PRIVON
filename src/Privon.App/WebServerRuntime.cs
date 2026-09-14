using System.Linq;
using System.Security.Principal;
using Privon.Browser;
using Privon.Windows;

namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate 031F6I.1 (E3 composition completion) -- owns the production Web channel host
/// server's full lifecycle: constructing the real graph (registry/manager/admission budget/real
/// named-pipe listener/real client-PID source/E2 binding source), starting its accept loop as a
/// tracked background <see cref="Task"/>, and the frozen shutdown barrier (Gate 031F6H section 22/23).
/// A single, isolated unit so <see cref="PrivonAppComposition"/>'s own startup/shutdown sequencing
/// stays a thin wrapper around it -- a Web server startup OR teardown failure here must never prevent
/// or unwind Windows clipboard protection (Gate 031F6I.1 B2).
///
/// Production admission budget capacity is exactly 8 (Gate 031F6H B3/section 6). This type creates
/// transport/session capability only (Gate 031F6H section 50); the exact extension-origin allowlist
/// and Web clipboard authorization policy remain outside this type.
///
/// SHARED_CHANNEL_TRUTH (Gate E5F): <see cref="StartOrNull(WebChannelRegistry, WebChannelManager)"/>
/// now takes the registry/manager as parameters rather than constructing its own private pair -- so
/// <see cref="PrivonAppComposition"/> can hand the SAME two instances to both this server AND a
/// composed <c>WebClipboardAuthorizationSource</c>, which must consult identical channel truth.
/// Constructing a <see cref="WebChannelRegistry"/>/<see cref="WebChannelManager"/> has no observable
/// side effect of its own (no thread, no I/O) -- only this method's OWN accept loop/pipe listener
/// still start no earlier than before, and only after Windows protection has already fully succeeded
/// (Gate 031F6I.1 B2, unchanged). <see cref="Manager"/> is exposed read-only, purely so a caller that
/// already owns the manager/registry pair can still discover which live instance this particular
/// server ended up running against.
/// </summary>
internal sealed class WebServerRuntime : IDisposable
{
    public static readonly TimeSpan StopTimeoutContractDefault = TimeSpan.FromSeconds(5);
    private const int AdmissionBudgetDefaultCapacity = 8;

    private readonly WebChannelManager _manager;
    private readonly WebSessionAdmissionBudget _budget;
    private readonly NamedPipeWebListener _listener;
    private readonly CancellationTokenSource _cts;
    private readonly Task _acceptLoop;

    private bool _disposed;

    /// <summary>The exact <see cref="WebChannelManager"/> this server was constructed against (Gate
    /// E5F SHARED_CHANNEL_TRUTH) -- the same instance a composed Web authorization source must use.</summary>
    public WebChannelManager Manager => _manager;

    private WebServerRuntime(
        WebChannelManager manager, WebSessionAdmissionBudget budget,
        NamedPipeWebListener listener, WebChannelHostServer server, CancellationTokenSource cts)
    {
        _manager = manager;
        _budget = budget;
        _listener = listener;
        _cts = cts;
        _acceptLoop = Task.Run(() => RunAcceptLoopAsync(server));
    }

    /// <summary>Constructs the remaining production graph (admission budget/real named-pipe listener/
    /// real client-PID source/E2 binding source) against the SUPPLIED <paramref name="registry"/>/
    /// <paramref name="manager"/> pair and starts the accept loop. Never throws -- any failure (the
    /// SID cannot be resolved, the pipe endpoint cannot be derived, etc.) is contained here and
    /// simply yields <see langword="null"/>, leaving Windows protection entirely unaffected (Gate
    /// 031F6I.1 B2).</summary>
    public static WebServerRuntime? StartOrNull(WebChannelRegistry registry, WebChannelManager manager)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(manager);

        try
        {
            string? sid = WindowsIdentity.GetCurrent().User?.Value;
            string? executablePath = Environment.ProcessPath;
            if (sid is null || executablePath is null)
                return null;

            string endpoint = WebPipeEndpoint.Compute(sid, executablePath);
            string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);

            var budget = new WebSessionAdmissionBudget(AdmissionBudgetDefaultCapacity);
            var listener = new NamedPipeWebListener(endpoint);
            var resolver = new BrowserHostBindingResolver(executablePath, systemDirectory);
            var bindingSource = new BrowserHostBindingSource(resolver);
            var pidSource = new Win32NamedPipeClientProcessIdSource();
            Func<Microsoft.Win32.SafeHandles.SafePipeHandle, uint?> clientProcessIdSource =
                handle => pidSource.TryGetClientProcessId(handle, out uint pid) ? pid : null;

            var server = new WebChannelHostServer(listener, clientProcessIdSource, bindingSource, registry, manager, budget);
            var cts = new CancellationTokenSource();

            return new WebServerRuntime(manager, budget, listener, server, cts);
        }
        catch
        {
            return null;
        }
    }

    private static async Task RunAcceptLoopAsync(WebChannelHostServer server)
    {
        try
        {
            await server.RunAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // The accept loop's own natural termination (cancellation, an exhausted consecutive-
            // failure budget, or a genuine fault) never faults this tracked task -- Dispose's own
            // bounded join below treats "the task completed" as success regardless of how it got
            // there.
        }
    }

    /// <summary>The frozen shutdown barrier (Gate 031F6H section 22/23): manager shutdown -> cancel
    /// -> dispose listener -> bounded accept-loop join -> snapshot+Close every live session -> bounded
    /// Lifecycle join -> dispose the admission budget LAST, and ONLY if the accept-loop join
    /// succeeded. If it did not, the budget is deliberately NOT disposed (the manager remains
    /// permanently stopping, so any late admission self-closes) and this method throws to signal
    /// that -- the caller (<see cref="PrivonAppComposition"/>) always contains this so Windows
    /// teardown continues regardless.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _manager.BeginShutdown();

        try { _cts.Cancel(); } catch { /* best-effort */ }
        try { _listener.Dispose(); } catch { /* best-effort */ }

        bool acceptLoopJoined = _acceptLoop.Wait(StopTimeoutContractDefault);

        var snapshot = _manager.SnapshotLive();
        foreach (var session in snapshot)
        {
            try { session.Close(); } catch { /* Close itself never throws; defensive only */ }
        }

        try
        {
            Task.WaitAll(snapshot.Select(s => s.Lifecycle).ToArray(), StopTimeoutContractDefault);
        }
        catch
        {
            // Best-effort bounded join only -- a lingering Lifecycle task is not this method's own
            // failure to report.
        }

        if (!acceptLoopJoined)
        {
            throw new InvalidOperationException(
                "WebServerRuntime: the Web channel accept loop did not stop within " +
                $"{StopTimeoutContractDefault}; the admission budget was NOT disposed and the manager " +
                "remains permanently stopping -- any late admission will self-close.");
        }

        _budget.Dispose();
    }
}
