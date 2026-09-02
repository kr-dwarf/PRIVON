using Microsoft.Win32.SafeHandles;

namespace Privon.Windows;

/// <summary>
/// PRIVON 0.3.1 Gate 031F6I (E3) -- test seam over the single Win32 named-pipe client-PID primitive
/// (GetNamedPipeClientProcessId), matching this codebase's existing native-seam pattern
/// (<see cref="IProcessTopologySource"/>, <see cref="IExecutableSignatureVerifier"/>). Only this one
/// mechanical fact belongs here (Gate 031F6H section 5) -- no other Win32 process-topology primitive.
/// The only production implementation is <see cref="Win32NamedPipeClientProcessIdSource"/>.
///
/// PUBLIC (Gate 031F6I.1 B2/B3): unlike <see cref="IProcessTopologySource"/> (which stays internal
/// because only <see cref="BrowserHostBindingResolver"/>'s own internals ever consume it),
/// Privon.App's own Web channel host server composition constructs and calls
/// <see cref="Win32NamedPipeClientProcessIdSource"/> directly -- exactly like
/// <see cref="BrowserHostBindingResolver"/>/<see cref="BrowserHostBinding"/> are already public for
/// the identical reason.
/// </summary>
public interface INamedPipeClientProcessIdSource
{
    /// <summary>The connected client process's PID for the supplied server-side pipe handle. Fails
    /// (<see langword="false"/>, <paramref name="processId"/> 0) rather than throwing when the
    /// underlying Win32 call fails.</summary>
    bool TryGetClientProcessId(SafePipeHandle pipeHandle, out uint processId);
}
