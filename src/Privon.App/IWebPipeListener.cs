using System.IO;
using Microsoft.Win32.SafeHandles;

namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate 031F6I (E3) -- <see cref="WebChannelHostServer"/>'s own accept-loop seam: one
/// mechanical "accept the next client" operation, returning the connected transport and its raw pipe
/// handle (for real client-PID extraction), or a null tuple on an ordinary pre-client accept failure.
/// Exists so the bounded accept-failure-count contract (Gate 031F6H section 20/47) and bad-client
/// resilience (R43) can be exercised deterministically without a real OS named pipe.
/// </summary>
internal interface IWebPipeListener : IDisposable
{
    Task<(Stream Stream, SafePipeHandle Handle)?> AcceptAsync(CancellationToken cancellationToken);
}
