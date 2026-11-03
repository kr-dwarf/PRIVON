using Privon.Detection;

namespace Privon.App;

// [PRIVON-AI-HANDOFF]
// ROLE: The ONE public assembly boundary an experimental opt-in host uses to run the real app with a country pack it owns (today: the U.S. opt-in).
// TRUTH: It hands the real PrivonAppComposition exactly the two delegates and the one flag the composition already understood internally, then runs the
//        same start / teardown sequence App.OnStartup / OnExit run. It adds no behaviour of its own: the policy, the tray, Settings and the decision
//        flow are the shipped ones. A host that never calls Create changes nothing -- the shipped startup is untouched.
// FROZEN: This session type knows no country implementation (the host supplies plain delegates and an IDisposable lifetime), exposes no composition, store,
//         bridge or setting, and starts nothing before TryStart. The single-instance mutex is the SHIPPED one, so an experimental session and the
//         shipped app can never run side by side (two clipboard listeners would double-process every copy).
// DO_NOT: Widen this into a general "configure the app" API, add reflection or a wider InternalsVisibleTo, or call it from the shipped startup path (which
//         uses PrivonAppComposition.CreateProduction -- the gated opt-in -- instead; this session has NO activation gate and is for the experimental host only).
// NAVIGATE: an experimental opt-in host (outside src) is the only caller; App.xaml.cs is the shipped sequence this mirrors.

/// <summary>What a host supplies for an experimental opt-in: the pipeline and the readiness the app should follow, and what to dispose with the session.</summary>
/// <param name="PipelineSource">Resolved at the start of every processing attempt, exactly like the composition's own pipeline source.</param>
/// <param name="ReadinessSource">Read on the UI thread whenever the tray line is rendered; never cached by the app.</param>
/// <param name="Lifetime">Disposed with the session, after the app's own teardown (null when there is nothing to dispose).</param>
public sealed record UsOptInSources(
    Func<DetectionPipeline> PipelineSource, Func<UsReadinessReport> ReadinessSource, IDisposable? Lifetime = null);

public sealed class PrivonOptInSession : IDisposable
{
    // The SAME mutex name the shipped App uses (a test pins that they are equal): the two can never run at once.
    internal const string SingleInstanceMutexName = "PRIVON-SingleInstance-3F2E9A7C-4B1D-4E3A-9C7A-8D6F1A2B3C4D";

    private readonly object _gate = new();
    private readonly PrivonAppComposition _composition;
    private readonly IDisposable? _lifetime;
    private readonly string _mutexName;
    private readonly Action<PrivonAppComposition> _startComposition;
    private readonly Func<PrivonAppComposition, PrivonAppUiBridge> _createBridge;

    private SingleInstanceGuard? _guard;
    private PrivonAppUiBridge? _bridge;
    private bool _startCalled;
    private bool _disposed;

    /// <summary>
    /// Builds a session around a real, NOT-yet-started composition. <paramref name="buildSources"/> receives the storage root the app will use (so the
    /// host reads the same country-choice file the Settings window writes) and returns the delegates; if it throws, nothing is created and nothing is
    /// started. <paramref name="storageRootPath"/> defaults to the app's production root; a different root is for experiments that must not touch the
    /// user's real settings. Nothing is started, subscribed or shown until <see cref="TryStart"/>.
    /// </summary>
    public static PrivonOptInSession Create(Func<string, UsOptInSources> buildSources, string? storageRootPath = null)
    {
        ArgumentNullException.ThrowIfNull(buildSources);
        string root = storageRootPath ?? PrivonAppComposition.ProductionStorageRootPath;
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("The storage root must not be empty.", nameof(storageRootPath));

        var sources = buildSources(root) ?? throw new InvalidOperationException("The host returned no sources.");
        PrivonAppComposition composition;
        try
        {
            ArgumentNullException.ThrowIfNull(sources.PipelineSource);
            ArgumentNullException.ThrowIfNull(sources.ReadinessSource);
            composition = PrivonAppComposition.CreateWithUsOptIn(root, sources.PipelineSource, sources.ReadinessSource);
        }
        catch
        {
            sources.Lifetime?.Dispose();
            throw;
        }

        return new PrivonOptInSession(composition, sources.Lifetime, SingleInstanceMutexName, c => c.Start(), PrivonAppUiBridge.CreateProduction);
    }

    // The seam the App tests use to exercise the lifecycle without starting a clipboard listener or showing a tray icon.
    internal PrivonOptInSession(
        PrivonAppComposition composition, IDisposable? lifetime, string mutexName,
        Action<PrivonAppComposition> startComposition, Func<PrivonAppComposition, PrivonAppUiBridge> createBridge)
    {
        ArgumentNullException.ThrowIfNull(composition);
        ArgumentException.ThrowIfNullOrEmpty(mutexName);
        ArgumentNullException.ThrowIfNull(startComposition);
        ArgumentNullException.ThrowIfNull(createBridge);
        _composition = composition;
        _lifetime = lifetime;
        _mutexName = mutexName;
        _startComposition = startComposition;
        _createBridge = createBridge;
    }

    internal PrivonAppComposition Composition => _composition;

    internal bool IsRunning
    {
        get { lock (_gate) return _bridge is not null && !_disposed; }
    }

    /// <summary>
    /// Starts the app exactly as the shipped startup does: take the single-instance mutex, start the composition, create and start the tray / Settings
    /// bridge. Returns <see langword="false"/> (starting nothing) when another PRIVON instance holds the mutex. A failure after the mutex is taken
    /// releases everything that was started and rethrows. Callable once.
    /// </summary>
    public bool TryStart()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_startCalled) throw new InvalidOperationException("TryStart has already been called on this session -- it is single-use.");
            _startCalled = true;
        }

        var guard = new SingleInstanceGuard(_mutexName);
        if (!guard.TryAcquire())
        {
            guard.Dispose();
            return false;
        }

        PrivonAppUiBridge bridge;
        try
        {
            _startComposition(_composition);
            bridge = _createBridge(_composition);
            bridge.Start();
        }
        catch
        {
            _composition.Dispose();
            guard.Dispose();
            throw;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                bridge.Dispose();
                _composition.Dispose();
                guard.Dispose();
                return false;
            }

            _guard = guard;
            _bridge = bridge;
        }

        return true;
    }

    /// <summary>Tears down in the shipped order -- UI and tray first, then the composition, then the host's lifetime, the mutex last. Idempotent.</summary>
    public void Dispose()
    {
        PrivonAppUiBridge? bridge;
        SingleInstanceGuard? guard;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            bridge = _bridge;
            guard = _guard;
            _bridge = null;
            _guard = null;
        }

        try { bridge?.Dispose(); }
        finally
        {
            try { _composition.Dispose(); }
            finally
            {
                try { _lifetime?.Dispose(); }
                finally { guard?.Dispose(); }
            }
        }
    }
}
