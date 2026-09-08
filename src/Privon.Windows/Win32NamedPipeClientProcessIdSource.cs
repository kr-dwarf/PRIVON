using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Privon.Windows;

/// <summary>
/// PRIVON 0.3.1 Gate 031F6I (E3) -- the sole production implementation of
/// <see cref="INamedPipeClientProcessIdSource"/>, wrapping exactly one Win32 primitive:
/// GetNamedPipeClientProcessId. No other Win32 process-topology call belongs in this type (Gate
/// 031F6H section 5) -- the mechanical browser-host binding/topology work remains exclusively
/// <see cref="BrowserHostBindingResolver"/>'s own job, consulted separately by the App layer once
/// this type has already produced the connecting client's PID.
/// </summary>
public sealed class Win32NamedPipeClientProcessIdSource : INamedPipeClientProcessIdSource
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafeHandle pipe, out uint clientProcessId);

    public bool TryGetClientProcessId(SafePipeHandle pipeHandle, out uint processId)
    {
        ArgumentNullException.ThrowIfNull(pipeHandle);
        return GetNamedPipeClientProcessId(pipeHandle, out processId);
    }
}
