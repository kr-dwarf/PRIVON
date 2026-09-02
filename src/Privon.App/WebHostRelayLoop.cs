using System.IO;
using Privon.Browser;

namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate 031F6I (E3) -- the dumb, mechanical, full-duplex Native Messaging host relay
/// (Gate 031F6H R22/R48): browser stdin bytes reach the pipe byte-identical, and pipe bytes reach
/// browser stdout byte-identical, using ONLY the existing, frozen <see cref="WebNativeMessagingPump"/>
/// -- this type carries no protocol/JSON awareness of any kind (all protocol decoding happens exactly
/// once, in the tray App's own channel session, never in this browser-spawned host process).
///
/// TERMINATION (Gate 031F6H R22/R23/R48): when ONE direction ends naturally (EOF or a fault), the
/// OTHER direction is given a bounded window to ALSO finish naturally -- e.g. a stdin that hits EOF
/// almost immediately must never cut off a pipe-&gt;stdout relay that is still actively delivering
/// bytes handed to it moments later -- before being forcibly unblocked (by disposing the pipe) once
/// that window elapses. Either way, <see cref="RunAsync"/> always returns in bounded time without
/// requiring <see cref="Environment.Exit(int)"/>. Deliberately depends on nothing beyond
/// <see cref="WebNativeMessagingPump"/> and raw <see cref="Stream"/>s (Gate 031F6H R24) -- no
/// App/Storage/clipboard/foreground/E2/Web-decode reference of any kind.
/// </summary>
internal static class WebHostRelayLoop
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);

    public static async Task RunAsync(Stream stdin, Stream stdout, Stream pipe)
    {
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(pipe);

        var stdinToPipe = RelayDirectionAsync(stdin, pipe);
        var pipeToStdout = RelayDirectionAsync(pipe, stdout);
        var both = Task.WhenAll(stdinToPipe, pipeToStdout);

        await Task.WhenAny(stdinToPipe, pipeToStdout).ConfigureAwait(false);

        // One direction ended naturally. Give the other a bounded window to also finish naturally
        // before forcibly unblocking it -- never assume the first direction ending means the whole
        // conversation is over.
        if (await Task.WhenAny(both, Task.Delay(DrainTimeout)).ConfigureAwait(false) != both)
        {
            try { pipe.Dispose(); } catch { /* forces the still-blocked direction to unblock */ }
            await Task.WhenAny(both, Task.Delay(DrainTimeout)).ConfigureAwait(false);
        }

        try { pipe.Dispose(); } catch { /* idempotent-ish cleanup; may already be disposed above */ }
    }

    private static async Task RelayDirectionAsync(Stream source, Stream destination)
    {
        try
        {
            while (true)
            {
                var status = await WebNativeMessagingPump.RelayOneFrameAsync(source, destination).ConfigureAwait(false);
                if (status != WebPumpRelayStatus.Relayed)
                    return;
            }
        }
        catch
        {
            // The sibling direction's completion disposed the shared pipe, or a genuine I/O fault
            // occurred -- either way this direction simply stops; never faults, never logs.
        }
    }
}
