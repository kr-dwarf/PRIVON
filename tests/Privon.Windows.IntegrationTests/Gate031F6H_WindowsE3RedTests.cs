using System.IO.Pipes;
using System.Reflection;
using Microsoft.Win32.SafeHandles;
using Privon.Windows;

namespace Privon.Windows.IntegrationTests;

// PRIVON 0.3.1 Gate 031F6H -- Phase E3 RED suite, Windows-only half: R6 (real LOCAL CurrentUserOnly
// named pipe connection) and R7 (INamedPipeClientProcessIdSource / Win32NamedPipeClientProcessIdSource
// over GetNamedPipeClientProcessId, against a real connected pipe). See
// Gate031F6H_E3RedTests.cs (Privon.App.Tests) for every other E3 requirement -- this file deliberately
// stays minimal and Windows-only, matching Gate031F6E_E2RedTests.cs's own scope discipline (E3 does not
// require a real browser or real E2 process tree for anything tested here).
//
// FROZEN API SURFACE (this file's own header):
//
//   internal interface Privon.Windows.INamedPipeClientProcessIdSource
//   {
//       bool TryGetClientProcessId(SafePipeHandle pipeHandle, out uint processId);
//   }
//
//   internal sealed class Privon.Windows.Win32NamedPipeClientProcessIdSource : INamedPipeClientProcessIdSource
//   {
//       public bool TryGetClientProcessId(SafePipeHandle pipeHandle, out uint processId); // GetNamedPipeClientProcessId
//   }
//
// FROZEN RED-ORDERING RULE (Gate 031F6H, applies to every test in this file, not merely the ones
// that locate a future CONCRETE class by reflection): a test must never enter a potentially blocking
// real-world operation (a real pipe wait, a real connection, a real handle-bound call) before it has
// first established that the future E3 production surface it is intended to exercise actually exists.
// R7 already located Win32NamedPipeClientProcessIdSource before touching any pipe. R6 is the ONE
// Windows-side production surface this file's own header freezes (WebChannelHostServer/
// IWebPipeListener are App-side and this project retains ZERO project reference to Privon.App -- R15's
// own frozen policy -- so they are not reachable here); R6's own stated purpose is "proves the exact
// mechanism a future WebChannelHostServer/IWebPipeListener will rely on" over the SAME connected pipe
// R7 then extracts a client PID from, via the SAME Win32NamedPipeClientProcessIdSource. R6 therefore
// gates on that identical production surface before ever touching a pipe -- exactly the same
// missing-implementation-observed-first discipline the R22 hotfix restored in the App-side suite, now
// applied uniformly to every test in this file, not merely the one that happened to reflect on a type.
//
// R4/R5 (WebPipeEndpoint) live in Privon.App and are tested in Gate031F6H_E3RedTests.cs -- this
// project retains zero project reference to Privon.App (R15's own frozen policy), so this file never
// references it.
public class Gate031F6H_WindowsE3RedTests
{
    private static Assembly WindowsAssembly => typeof(BrowserHostBindingStatus).Assembly;

    private const string PidSourceInterfaceName = "Privon.Windows.INamedPipeClientProcessIdSource";
    private const string Win32PidSourceTypeName = "Privon.Windows.Win32NamedPipeClientProcessIdSource";

    private static Type? PidSourceInterfaceType => WindowsAssembly.GetType(PidSourceInterfaceName);
    private static Type? Win32PidSourceType => WindowsAssembly.GetType(Win32PidSourceTypeName);

    private const string PidSourceContract =
        "Privon.Windows.INamedPipeClientProcessIdSource / Win32NamedPipeClientProcessIdSource do not " +
        "exist yet (Gate 031F6H E3, frozen contract). Required: internal interface " +
        "INamedPipeClientProcessIdSource { bool TryGetClientProcessId(SafePipeHandle pipeHandle, out " +
        "uint processId); } and internal sealed class Win32NamedPipeClientProcessIdSource implementing " +
        "it via GetNamedPipeClientProcessId ONLY -- no other Win32 process-topology primitive belongs " +
        "in this type (section 5).";

    /// <summary>RED_LOCATE: throws (via the real Assert.True failure) before any pipe is ever
    /// constructed unless both the interface and its Win32 implementation already exist and the
    /// implementation genuinely implements the interface. Shared by R6 and R7 -- see this file's own
    /// FROZEN RED-ORDERING RULE doc for why R6 depends on this exact same production surface.</summary>
    private static void RequirePidSourceProductionSurface(string requiredBehavior)
    {
        Assert.True(PidSourceInterfaceType is not null && Win32PidSourceType is not null,
            PidSourceContract + " REQUIRED FOR THIS TEST: " + requiredBehavior);

        var interfaceMethod = PidSourceInterfaceType!.GetMethod("TryGetClientProcessId", BindingFlags.Public | BindingFlags.Instance);
        Assert.True(interfaceMethod is not null,
            $"{PidSourceInterfaceName} exists but exposes no TryGetClientProcessId(...) method. " + PidSourceContract);

        Assert.True(PidSourceInterfaceType!.IsAssignableFrom(Win32PidSourceType),
            $"{Win32PidSourceTypeName} must implement {PidSourceInterfaceName}.");
    }

    /// <summary>
    /// Establishes a real local pipe connection with a hard, guaranteed-effective deadline.
    /// NamedPipeServerStream.WaitForConnectionAsync's own CancellationToken support has a known
    /// platform gap: it does not reliably unblock the underlying native ConnectNamedPipe wait, so a
    /// CancellationTokenSource alone is not sufficient to bound this operation. Disposing the pipe
    /// streams IS guaranteed to release a blocked native call, so a timeout here forcibly disposes
    /// both sides rather than trusting cancellation alone -- the standard, robust way to bound an
    /// otherwise-uncancellable blocking pipe wait. Deliberately no sleep/poll loop: exactly one
    /// Task.WhenAny race against one bounded delay. Only ever reached AFTER
    /// RequirePidSourceProductionSurface has already confirmed the required future type exists (see
    /// R6/R7 below), matching this file's own FROZEN RED-ORDERING RULE -- section 4's "No unbounded
    /// wait is allowed anywhere in the test path" is satisfied unconditionally, independent of that
    /// ordering, purely by this bounded race.
    /// </summary>
    private static async Task ConnectWithHardDeadlineAsync(NamedPipeServerStream server, NamedPipeClientStream client, TimeSpan timeout)
    {
        var acceptTask = server.WaitForConnectionAsync();
        var connectTask = client.ConnectAsync((int)timeout.TotalMilliseconds);
        var combined = Task.WhenAll(acceptTask, connectTask);

        var winner = await Task.WhenAny(combined, Task.Delay(timeout));
        if (winner != combined)
        {
            try { server.Dispose(); } catch { /* best-effort forced unblock */ }
            try { client.Dispose(); } catch { /* best-effort forced unblock */ }
            throw new TimeoutException($"R6/R7: local named pipe connection did not complete within {timeout}.");
        }

        await combined; // propagate any real connection fault.
    }

    // ==================================================================
    // R6 -- real LOCAL named pipe, CurrentUserOnly on both roles. Proves the exact mechanism a future
    // WebChannelHostServer/IWebPipeListener will rely on, over the SAME Win32NamedPipeClientProcessIdSource
    // production surface R7 exercises -- gated on that surface's existence first (see this file's own
    // FROZEN RED-ORDERING RULE doc), so a missing E3 implementation is observed immediately rather than
    // by ever touching a real, potentially blocking pipe wait.
    // ==================================================================

    [Fact]
    public async Task R6_CurrentUserOnlyNamedPipe_LegitimateSameUserClientServerConnection_Succeeds()
    {
        RequirePidSourceProductionSurface(
            "a legitimate same-user local CurrentUserOnly client/server pipe connection must succeed, " +
            "proving the exact mechanism the future Win32NamedPipeClientProcessIdSource (R7) is bound " +
            "to run over -- structural assertion: E3 uses CurrentUserOnly for both roles.");

        string pipeName = "PRIVON-E3-RED-" + Guid.NewGuid().ToString("N");

        // TEST_CONTRACT_CORRECTION (Gate 031F6I.3): the constructor overload that leaves in/out
        // buffer sizes at their unspecified default causes a real data write on the connected pipe to
        // hang indefinitely (forensically isolated -- the connection itself always succeeded; only
        // the first write ever blocked). Explicit, non-zero buffer sizes are required for the pipe to
        // actually function as a real full-duplex byte channel -- the SAME fix applied to the
        // production listener (Gate 031F6I.3 Task 1), so this test now models the working production
        // pipe configuration rather than a broken one.
        using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            inBufferSize: 4096, outBufferSize: 4096);

        using var client = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        await ConnectWithHardDeadlineAsync(server, client, TimeSpan.FromSeconds(10));

        Assert.True(server.IsConnected);
        Assert.True(client.IsConnected);

        byte[] payload = [1, 2, 3, 4];
        await client.WriteAsync(payload);
        await client.FlushAsync();

        byte[] received = new byte[payload.Length];
        int total = 0;
        while (total < received.Length)
        {
            int read = await server.ReadAsync(received.AsMemory(total));
            Assert.True(read > 0);
            total += read;
        }

        Assert.Equal(payload, received);
    }

    // ==================================================================
    // R7 -- client PID over a real connected pipe.
    // ==================================================================

    [Fact]
    public async Task R7_Win32NamedPipeClientProcessIdSource_ReturnsTheActualConnectedClientPid()
    {
        RequirePidSourceProductionSurface(
            "TryGetClientProcessId over a real, connected local pipe must return the actual connected " +
            "client PID.");

        object? sourceInstance = Activator.CreateInstance(Win32PidSourceType!);
        Assert.True(sourceInstance is not null, $"{Win32PidSourceTypeName} could not be constructed via its parameterless constructor.");

        string pipeName = "PRIVON-E3-RED-R7-" + Guid.NewGuid().ToString("N");

        // TEST_CONTRACT_CORRECTION (Gate 031F6I.3): same explicit buffer sizes as R6 -- see that
        // test's own comment for the forensic evidence. GetNamedPipeClientProcessId itself never
        // touches the pipe's data buffer, so this test never actually hit the defect, but it should
        // still model the same working production pipe configuration as R6 for consistency.
        using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            inBufferSize: 4096, outBufferSize: 4096);
        using var client = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        await ConnectWithHardDeadlineAsync(server, client, TimeSpan.FromSeconds(10));

        var method = Win32PidSourceType!.GetMethod("TryGetClientProcessId", BindingFlags.Public | BindingFlags.Instance);
        Assert.True(method is not null, PidSourceContract);

        var args = new object?[] { server.SafePipeHandle, (uint)0 };
        bool result = (bool)method!.Invoke(sourceInstance, args)!;
        uint reportedPid = (uint)args[1]!;

        Assert.True(result, "R7: TryGetClientProcessId must succeed over a real, connected local pipe.");
        Assert.Equal((uint)Environment.ProcessId, reportedPid);
    }
}
