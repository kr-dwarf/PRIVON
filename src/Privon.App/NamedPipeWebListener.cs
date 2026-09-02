using System.IO;
using System.IO.Pipes;
using Microsoft.Win32.SafeHandles;

namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate 031F6I.1 (E3 composition completion) -- the sole production
/// <see cref="IWebPipeListener"/> implementation: a real local named pipe, one fresh
/// <see cref="NamedPipeServerStream"/> instance per accepted connection, always with
/// <see cref="PipeOptions.CurrentUserOnly"/> on both roles (Gate 031F6H section 5/R6) and
/// <see cref="NamedPipeServerStream.MaxAllowedServerInstances"/> as the OS instance-count parameter --
/// the OS instance count is never product flow control; <see cref="WebSessionAdmissionBudget"/> is the
/// one and only admission gate (Gate 031F6H B3).
/// </summary>
internal sealed class NamedPipeWebListener : IWebPipeListener
{
    // Gate 031F6I.3 -- CONFIRMED PRODUCTION DEFECT, forensically isolated and fixed: the constructor
    // overload that leaves in/out buffer sizes at their unspecified default causes ANY write on the
    // connected pipe (either direction, sync or async) to block indefinitely -- the connection itself
    // succeeds; only the first real data write ever hangs. Explicit, non-zero buffer sizes are
    // therefore REQUIRED, not cosmetic. 4096 is the frozen production contract for E3 unless later
    // evidence proves it insufficient.
    private const int PipeBufferSize = 4096;

    private readonly string _pipeName;
    private readonly object _gate = new();
    private NamedPipeServerStream? _current;
    private bool _disposed;

    public NamedPipeWebListener(string pipeName)
    {
        ArgumentException.ThrowIfNullOrEmpty(pipeName);
        _pipeName = pipeName;
    }

    public async Task<(Stream Stream, SafePipeHandle Handle)?> AcceptAsync(CancellationToken cancellationToken)
    {
        NamedPipeServerStream server;
        lock (_gate)
        {
            if (_disposed)
                return null;

            server = new NamedPipeServerStream(
                _pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                inBufferSize: PipeBufferSize, outBufferSize: PipeBufferSize);
            _current = server;
        }

        try
        {
            await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try { server.Dispose(); } catch { /* best-effort */ }
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);
            return null; // an ordinary pre-client accept failure -- counted by the caller's own bound.
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_current, server))
                    _current = null;
            }
        }

        return (server, server.SafePipeHandle);
    }

    /// <summary>Forces any in-flight <see cref="AcceptAsync"/> to unblock: disposing the currently
    /// listening stream releases its blocked native wait, mirroring the same technique this gate's
    /// own frozen test harness already relies on for exactly the same platform reason (Gate 031F6H
    /// R6's own <c>ConnectWithHardDeadlineAsync</c> doc).</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            try { _current?.Dispose(); } catch { /* best-effort */ }
            _current = null;
        }
    }
}
