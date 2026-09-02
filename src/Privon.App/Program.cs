using System.IO;
using System.IO.Pipes;
using System.Security.Principal;

namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate 031F6I (E3) -- the real process entry point (wired via
/// <c>&lt;StartupObject&gt;Privon.App.Program&lt;/StartupObject&gt;</c> in Privon.App.csproj, replacing
/// WPF's own generated <c>App.Main</c> -- Gate 031F6H R29). Dispatches through
/// <see cref="PrivonEntryPoint.Run"/> against the real, empty
/// <see cref="WebExtensionOriginAllowlist.Production"/>: a normal launch replicates exactly what the
/// WPF-generated Main would have done (construct <see cref="App"/>, initialize, run); a valid host
/// invocation runs the minimal Native Messaging host relay path and NOTHING else -- no
/// <see cref="App"/>, no <see cref="SingleInstanceGuard"/>, no <see cref="PrivonAppComposition"/> (Gate
/// 031F6H R2/R24).
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        int exitCode = 0;

        PrivonEntryPoint.Run(
            args,
            WebExtensionOriginAllowlist.Production,
            normalRunner: () => exitCode = RunNormalTray(),
            hostRunner: () => exitCode = RunHostMode());

        return exitCode;
    }

    private static int RunNormalTray()
    {
        var app = new App();
        return app.Run();
    }

    private static int RunHostMode()
    {
        try
        {
            RunHostModeAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // No diagnostic byte of any kind may reach stdout (Gate 031F6H R23) -- a host-mode
            // failure (no running tray server, connection refused, etc.) simply ends the process.
        }

        return 0;
    }

    private static async Task RunHostModeAsync()
    {
        string? sid = WindowsIdentity.GetCurrent().User?.Value;
        string? executablePath = Environment.ProcessPath;
        if (sid is null || executablePath is null)
            return;

        string endpoint = WebPipeEndpoint.Compute(sid, executablePath);

        using var pipe = new NamedPipeClientStream(
            ".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        try
        {
            await pipe.ConnectAsync(5000).ConfigureAwait(false);
        }
        catch
        {
            return; // No running server, or connection refused -- host mode simply ends.
        }

        using Stream stdin = Console.OpenStandardInput();
        using Stream stdout = Console.OpenStandardOutput();

        await WebHostRelayLoop.RunAsync(stdin, stdout, pipe).ConfigureAwait(false);
    }
}
