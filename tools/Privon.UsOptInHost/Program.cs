using System.Windows;
using Privon.App;

namespace Privon.UsOptInHost;

// [PRIVON-AI-HANDOFF]
// ROLE: The entry point of the EXPERIMENTAL host. Run only on purpose: Privon.UsOptInHost.exe --experimental-us-opt-in [--storage-root <dir>].
// TRUTH: It assembles the real app (tray, Settings, clipboard processing) with the U.S. opt-in and runs it. It does NOT make the shipped app US-enabled,
//        does NOT authenticate the country setting, does NOT block a paste or a send, and has not been run against a real clipboard by its authors --
//        the checks in the repository stop short of starting the clipboard listener.
// FROZEN: No opt-in flag -> nothing is created and nothing runs (exit 2). The shipped single-instance mutex is used, so this and the shipped app can
//         never run together. Exit codes: 0 ran and ended, 2 not opted in, 3 could not be assembled, 4 another PRIVON instance is running / not started.
// DO_NOT: Start the app without the flag, add a second entry path, or reference this project from any src project or the release script.

public static class Program
{
    public const int ExitNotOptedIn = 2;
    public const int ExitNotAssembled = 3;
    public const int ExitNotStarted = 4;

    [STAThread]
    public static int Main(string[] args)
    {
        var options = HostOptions.Parse(args);
        if (!options.ExplicitOptIn) return ExitNotOptedIn;

        PrivonOptInSession session;
        try
        {
            session = PrivonOptInSession.Create(UsOptInSourcesBuilder.BuildProduction, options.StorageRoot);
        }
        catch
        {
            return ExitNotAssembled;       // an unusable authority, an unreadable store: nothing was created and nothing was started
        }

        bool started = false;
        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Startup += (_, _) =>
            {
                try { started = session.TryStart(); }
                catch { started = false; }
                if (!started) app.Shutdown();
            };
            app.Exit += (_, _) => session.Dispose();
            app.Run();
        }
        finally
        {
            session.Dispose();
        }

        return started ? 0 : ExitNotStarted;
    }
}
