using System.IO;
using Microsoft.Win32.SafeHandles;
using Privon.Browser;
using Privon.Windows;

namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate 031F6I (E3) -- the per-connection admission pipeline (real client PID -&gt; E2
/// browser-host binding -&gt; <see cref="WebBrowserGate"/> policy -&gt; <see cref="WebChannelSession"/>
/// construction/admission) plus the bounded accept loop that feeds it (Gate 031F6H sections 7-10,
/// 20, 23, R36-R43).
///
/// OWNERSHIP (Gate 031F6H section 7): before a <see cref="WebChannelSession"/> is successfully
/// constructed, THIS type owns the admitted stream, the resolved binding, and the admission Lease --
/// every rejection path (PID failure, E2 rejection, policy rejection, or a session-construction
/// failure) disposes exactly those three, in binding-then-transport-then-Lease order, via a single
/// <c>finally</c>. The instant construction succeeds, ownership transfers COMPLETELY to the session;
/// this type never reclaims them again.
/// </summary>
internal sealed class WebChannelHostServer
{
    public const int MaxConsecutiveAcceptFailuresContractDefault = 8;

    private readonly IWebPipeListener _listener;
    private readonly Func<SafePipeHandle, uint?> _clientProcessIdSource;
    private readonly IWebHostBindingSource _bindingSource;
    private readonly WebChannelRegistry _registry;
    private readonly WebChannelManager _manager;
    private readonly WebSessionAdmissionBudget _budget;

    public WebChannelHostServer(
        IWebPipeListener listener,
        Func<SafePipeHandle, uint?> clientProcessIdSource,
        IWebHostBindingSource bindingSource,
        WebChannelRegistry registry,
        WebChannelManager manager,
        WebSessionAdmissionBudget budget)
    {
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(clientProcessIdSource);
        ArgumentNullException.ThrowIfNull(bindingSource);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(budget);

        _listener = listener;
        _clientProcessIdSource = clientProcessIdSource;
        _bindingSource = bindingSource;
        _registry = registry;
        _manager = manager;
        _budget = budget;
    }

    /// <summary>Runs the entire per-connection admission pipeline for one already-accepted client.
    /// Never throws. Ownership of <paramref name="clientStream"/>/<paramref name="lease"/> (and,
    /// once resolved, the binding) transfers fully to the constructed session on success; every
    /// rejection path disposes them here instead.</summary>
    public Task AdmitConnectionAsync(
        Stream clientStream,
        SafePipeHandle clientHandle,
        WebSessionAdmissionBudget.Lease lease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clientStream);
        ArgumentNullException.ThrowIfNull(clientHandle);
        ArgumentNullException.ThrowIfNull(lease);

        IWebBrowserHostBinding? binding = null;
        bool transferred = false;

        try
        {
            uint? pid = _clientProcessIdSource(clientHandle);
            if (pid is null)
                return Task.CompletedTask; // R36 -- rejected before the E2 binding source is ever consulted.

            var status = _bindingSource.Resolve(pid.Value, out binding);
            if (status != BrowserHostBindingStatus.Resolved || binding is null)
                return Task.CompletedTask; // R37

            if (!WebBrowserGate.TryIdentifySupportedBrowser(
                    binding.BrowserProcessName, binding.BrowserPackageIdentity,
                    binding.BrowserExecutableSignature, binding.BrowserSignerOrganization,
                    out var browser)
                || !ReleaseBrowserSupportPolicy.IsSupported(browser))
                return Task.CompletedTask; // R38/R9 -- no Hello ever consumed.

            var session = new WebChannelSession(clientStream, binding, _registry, lease, _manager);
            transferred = true; // Ownership transfer point (section 7) -- construction succeeded.
            _manager.BeginAdmission(session);
            return Task.CompletedTask;
        }
        finally
        {
            if (!transferred)
            {
                SafeInvoke(() => binding?.Dispose());
                SafeInvoke(clientStream.Dispose);
                SafeInvoke(lease.Dispose);
            }
        }
    }

    /// <summary>The bounded accept loop (Gate 031F6H section 20/47): a consecutive run of pre-client
    /// accept failures terminates the loop after <see cref="MaxConsecutiveAcceptFailuresContractDefault"/>;
    /// a successful connection resets that counter; failures occurring AFTER a client has connected
    /// (PID/E2/policy/handshake) never consume it, since they are handled entirely inside
    /// <see cref="AdmitConnectionAsync"/>, which this loop never awaits (a bad client must never stall
    /// or kill the listener -- R43).</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        int consecutiveFailures = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var lease = await _budget.AcquireAsync(cancellationToken).ConfigureAwait(false);

            (Stream Stream, SafePipeHandle Handle)? accepted;
            try
            {
                accepted = await _listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                lease.Dispose();
                throw;
            }
            catch
            {
                lease.Dispose();
                if (++consecutiveFailures >= MaxConsecutiveAcceptFailuresContractDefault)
                    return;
                continue;
            }

            if (accepted is null)
            {
                lease.Dispose();
                if (++consecutiveFailures >= MaxConsecutiveAcceptFailuresContractDefault)
                    return;
                continue;
            }

            consecutiveFailures = 0;

            _ = AdmitConnectionAsync(accepted.Value.Stream, accepted.Value.Handle, lease, cancellationToken);
        }
    }

    private static void SafeInvoke(Action action)
    {
        try { action(); }
        catch
        {
            // Best-effort cleanup only -- never allowed to mask/replace the admission outcome.
        }
    }
}
