using System.Windows;

namespace Privon.App;

/// <summary>
/// Phase 3C STEP36 -- thin WPF lifecycle boundary. Owns exactly one
/// <see cref="PrivonAppComposition"/> instance; every actual wiring/start/shutdown decision lives in
/// that plain-CLR type (see its own class doc), never here. WPF_PROCESS_LIFETIME_DECISION (Phase 3C
/// STEP35.1, frozen): <see cref="Application.ShutdownMode"/> is set to
/// <see cref="ShutdownMode.OnExplicitShutdown"/> before the composition root is ever constructed --
/// there is no <c>StartupUri</c>/<c>MainWindow</c> of any kind in this 0.1 build, so the process must
/// never rely on WPF's own last-window-closed default to decide when to exit.
///
/// Phase 3C STEP40 adds the second, equally thin root this type owns:
/// <see cref="PrivonAppUiBridge"/> (the minimal NeedsDecision Protect UI + tray running/Exit
/// surface), constructed only AFTER <see cref="PrivonAppComposition.Start"/> has already succeeded
/// (it depends on that composition's now-non-null <see cref="PrivonAppComposition.SessionPublisher"/>/
/// <see cref="PrivonAppComposition.Lifecycle"/>/<see cref="PrivonAppComposition.Resolver"/>). This
/// type still contains no decision-resolution loop, no Windows P/Invoke, and no lifecycle policy of
/// its own -- both roots are fully self-contained; this type only sequences their construction and
/// teardown.
///
/// STARTUP_FAILURE (Phase 3C STEP36, revised STEP40/STEP40.2, frozen): if <see cref="PrivonAppComposition.Start"/>
/// throws, that exception propagates out of <see cref="OnStartup"/> unchanged -- <see cref="_composition"/>
/// is never assigned (its own <c>Start</c> has already rolled back whatever it built), so no
/// protection pipeline remains active and <see cref="OnExit"/> later finds nothing to clean up. If
/// the composition succeeds but <see cref="PrivonAppUiBridge.CreateProduction"/>/<see cref="PrivonAppUiBridge.Start"/>
/// then fails (Phase 3C STEP40 instruction: a usable 0.1 needs its UI/tray surface, so its failure
/// must fail the whole startup, never continue as an invisible partially-usable process),
/// <see cref="PrivonAppUiBridge.Start"/> has ALREADY rolled back whatever of its own three stages
/// (prompt-coordinator start / tray Exit subscription / tray show) succeeded (Phase 3C STEP40.2's
/// own STARTUP_ROLLBACK, mirroring <see cref="PrivonAppComposition"/>'s identical discipline) by
/// the time this method's own <c>catch</c> runs -- this method then only needs to dispose the
/// already-started <paramref name="composition"/> and rethrow the ORIGINAL exception unchanged
/// (never masked/wrapped). <see cref="_composition"/>/<see cref="_uiBridge"/> are only assigned
/// once BOTH roots have succeeded, so a failed startup never leaves either field pointing at
/// something no longer stood behind ("no degraded success state"). STARTUP_FAILURE_USER_PRESENTATION
/// remains deliberately unimplemented (no custom error dialog) -- out of scope for this STEP.
/// </summary>
public partial class App : System.Windows.Application
{
    private PrivonAppComposition? _composition;
    private PrivonAppUiBridge? _uiBridge;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var composition = new PrivonAppComposition();
        composition.Start();

        PrivonAppUiBridge uiBridge;
        try
        {
            uiBridge = PrivonAppUiBridge.CreateProduction(composition);
            uiBridge.Start();
        }
        catch
        {
            composition.Dispose();
            throw;
        }

        _composition = composition;
        _uiBridge = uiBridge;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // SHUTDOWN_ORDER (Phase 3C STEP40 instruction): the UI/tray surface is torn down first --
        // prevents any new decision-prompt dispatch, closes any currently-visible prompt, removes
        // the tray icon -- strictly before the composition root's own already-frozen safe shutdown
        // ordering (session-lock observer -> coordinator -> verifier -> lifecycle -> reader ->
        // monitor -> gate) begins.
        _uiBridge?.Dispose();
        _uiBridge = null;

        _composition?.Dispose();
        _composition = null;

        base.OnExit(e);
    }
}
