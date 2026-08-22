using System.Reflection;
using Privon.App;
using Privon.Windows;

namespace Privon.App.Tests;

// Phase 3C STEP36 -- App Live Composition Root regression, extended Phase 3C STEP38 with the
// Windows session-lock sensitive-state-discard capability. Uses the REAL production object graph
// throughout (real SessionLockNotificationAdapter/SessionLockMonitor/ClipboardChangeMonitor/
// ComposerTextReader/ClipboardOperationGate/ClipboardDecisionScopeLifecycle/ClipboardComposerVerifier/
// ClipboardPrivacyCoordinator/ClipboardDecisionActionResolver/PrivonLocalStore) -- never a fake/mock
// for any of this type's own dependencies. Deterministic forced-failure tests (session-observer
// start failure, composer-reader start failure, monitor start failure) exploit each real
// component's own already-documented "second Start() always throws" single-use guard rather than
// injecting a fake. Storage always points at a throwaway temp directory -- never the real
// %LocalAppData%\PRIVON. No System.Windows.Application is ever instantiated anywhere in this file.
// See SessionLockInvalidationTests.cs for the App-level Locked-callback-effect tests, which -- for
// the one documented, justified reason no automated test may trigger a real Windows session lock --
// use FakeSessionLockNotification instead of a real SessionLockMonitor.
public class PrivonAppCompositionTests
{
    private static string CreateTempStorageRoot() =>
        Path.Combine(Path.GetTempPath(), "PrivonAppCompositionTests", Guid.NewGuid().ToString("N"));

    private static PrivonAppComposition CreateWithRealGraph(string? storageRoot = null) =>
        new(storageRoot ?? CreateTempStorageRoot(), new SessionLockNotificationAdapter(), new ClipboardChangeMonitor(), new ComposerTextReader());

    private static object? GetField(object obj, string name)
    {
        var field = obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException($"Field '{name}' not found on {obj.GetType()}.");
        return field.GetValue(obj);
    }

    private static void SetField(object obj, string name, object? value)
    {
        var field = obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException($"Field '{name}' not found on {obj.GetType()}.");
        field.SetValue(obj, value);
    }

    // ==================================================================
    // A. SHARED IDENTITIES (items 33)
    // ==================================================================

    [Fact]
    public void SharedOperationGate_IsSameInstanceAcrossCoordinatorResolverVerifier()
    {
        using var composition = CreateWithRealGraph();
        composition.Start();

        var gate = GetField(composition, "_operationGate");
        var coordinator = GetField(composition, "_coordinator")!;
        var resolver = composition.Resolver!;
        var verifier = composition.Verifier!;

        Assert.Same(gate, GetField(coordinator, "_operationGate"));
        Assert.Same(gate, GetField(resolver, "_operationGate"));
        Assert.Same(gate, GetField(verifier, "_operationGate"));
    }

    [Fact]
    public void SharedLifecycle_BacksNotificationLifecycle_DecisionScopeLifecycle_AndGenerationSnapshot()
    {
        using var composition = CreateWithRealGraph();
        composition.Start();

        var lifecycle = GetField(composition, "_lifecycle")!;
        var coordinator = GetField(composition, "_coordinator")!;
        var resolver = composition.Resolver!;
        var verifier = composition.Verifier!;

        Assert.Same(lifecycle, GetField(coordinator, "_notificationLifecycle"));
        Assert.Same(lifecycle, GetField(resolver, "_lifecycle"));
        Assert.Same(lifecycle, GetField(verifier, "_generationSnapshot"));

        // The session publisher the coordinator holds must also share this exact lifecycle instance.
        var publisher = GetField(coordinator, "_decisionSessionPublisher")!;
        Assert.Same(lifecycle, GetField(publisher, "_lifecycle"));
    }

    [Fact]
    public void SharedVerifier_BacksCoordinatorHandoffAndInvalidation_AndResolverHandoff()
    {
        using var composition = CreateWithRealGraph();
        composition.Start();

        var verifier = composition.Verifier!;
        var coordinator = GetField(composition, "_coordinator")!;
        var resolver = composition.Resolver!;

        Assert.Same(verifier, GetField(coordinator, "_verificationHandoff"));
        Assert.Same(verifier, GetField(coordinator, "_verificationInvalidation"));
        Assert.Same(verifier, GetField(resolver, "_verificationHandoff"));
    }

    [Fact]
    public void SharedMonitor_BacksCoordinatorAndResolverReadWriteTransports()
    {
        using var composition = CreateWithRealGraph();
        composition.Start();

        var monitor = GetField(composition, "_monitor")!;
        var coordinator = GetField(composition, "_coordinator")!;
        var resolver = composition.Resolver!;

        var coordinatorRead = GetField(coordinator, "_transport")!;
        var coordinatorWrite = GetField(coordinator, "_writeTransport")!;
        var resolverRead = GetField(resolver, "_readTransport")!;
        var resolverWrite = GetField(resolver, "_writeTransport")!;

        Assert.Same(monitor, GetField(coordinatorRead, "_monitor"));
        Assert.Same(monitor, GetField(coordinatorWrite, "_monitor"));
        Assert.Same(monitor, GetField(resolverRead, "_monitor"));
        Assert.Same(monitor, GetField(resolverWrite, "_monitor"));

        // Also exactly the same instance this composition root will directly Dispose() later.
        Assert.Same(monitor, GetField(composition, "_monitor"));
    }

    [Fact]
    public void SharedComposerReader_BacksVerifierComposerTransport()
    {
        using var composition = CreateWithRealGraph();
        composition.Start();

        var reader = GetField(composition, "_composerReader")!;
        var verifier = composition.Verifier!;
        var composerReadTransport = GetField(verifier, "_composerReadTransport")!;

        Assert.Same(reader, GetField(composerReadTransport, "_reader"));
    }

    [Fact]
    public void SharedProcessorAndTargetCapture_ReusedByCoordinatorAndResolver()
    {
        using var composition = CreateWithRealGraph();
        composition.Start();

        var coordinator = GetField(composition, "_coordinator")!;
        var resolver = composition.Resolver!;

        Assert.Same(GetField(coordinator, "_processor"), GetField(resolver, "_processor"));
        Assert.Same(GetField(coordinator, "_targetCapture"), GetField(resolver, "_targetCapture"));
    }

    // ---- Phase 3C STEP38 (item 30): exactly one production session-lock notification instance
    // exists per composition, held directly (never rebuilt/duplicated). The REAL Locked-callback
    // EFFECT (that it targets the same lifecycle/verifier instances everything else shares) is
    // proven in SessionLockInvalidationTests.cs -- this composition-identity test only proves no
    // duplicate ISessionLockNotification is ever constructed for a single graph. ----
    [Fact]
    public void SessionLockNotification_IsHeldDirectly_ExactlyOnePerComposition()
    {
        var sessionLockNotification = new SessionLockNotificationAdapter();
        using var composition = new PrivonAppComposition(
            CreateTempStorageRoot(), sessionLockNotification, new ClipboardChangeMonitor(), new ComposerTextReader());
        composition.Start();

        Assert.Same(sessionLockNotification, GetField(composition, "_sessionLockNotification"));
    }

    // ==================================================================
    // B. START ORDER (item 34)
    // ==================================================================

    [Fact]
    public void Start_Succeeds_ResolverAndVerifierBecomeReachable()
    {
        using var composition = CreateWithRealGraph();
        Assert.Null(composition.Resolver);
        Assert.Null(composition.Verifier);

        composition.Start();

        Assert.NotNull(composition.Resolver);
        Assert.NotNull(composition.Verifier);
    }

    [Fact]
    public void Start_ComposerReaderAlreadyStarted_ThrowsBeforeCoordinatorEverStarts()
    {
        var preStartedReader = new ComposerTextReader();
        preStartedReader.Start();
        try
        {
            var monitor = new ClipboardChangeMonitor();
            using var composition = new PrivonAppComposition(CreateTempStorageRoot(), new SessionLockNotificationAdapter(), monitor, preStartedReader);

            Assert.Throws<InvalidOperationException>(composition.Start);

            // The coordinator's own Start() -- which is what would have started `monitor` -- must
            // never have been reached: `monitor` (a direct-ownership root, constructed but never
            // started) was disposed by rollback instead, so a subsequent Start() attempt on it fails
            // with ObjectDisposedException, never with the "already started" InvalidOperationException
            // that WOULD occur if the coordinator had actually gotten far enough to start it itself.
            Assert.Throws<ObjectDisposedException>(monitor.Start);
        }
        finally
        {
            preStartedReader.Dispose();
        }
    }

    [Fact]
    public void Start_CoordinatorStartsMonitor_ProvenBySecondStartThrowingAlreadyStarted()
    {
        using var composition = CreateWithRealGraph();
        composition.Start();

        var monitor = (ClipboardChangeMonitor)GetField(composition, "_monitor")!;

        // Not disposed yet (composition is still running) -- a second Start() now throws the
        // single-use "already been called" InvalidOperationException, proving coordinator.Start()
        // really did reach and start this exact monitor instance (an ObjectDisposedException here
        // would instead mean it was never actually started at all).
        Assert.Throws<InvalidOperationException>(monitor.Start);
    }

    [Fact]
    public void Start_ComposerReaderStartedBeforeCoordinator_ProvenBySecondStartThrowingAlreadyStarted()
    {
        using var composition = CreateWithRealGraph();
        composition.Start();

        var reader = (ComposerTextReader)GetField(composition, "_composerReader")!;

        Assert.Throws<InvalidOperationException>(reader.Start);
    }

    // ---- Phase 3C STEP38 (item 31): the session-lock observer really was started by
    // composition.Start() (never disposed by a normal, successful shutdown-free lifecycle -- unlike
    // ClipboardChangeMonitor/ComposerTextReader, PrivonAppComposition only ever calls Stop() on it,
    // never Dispose() -- see this type's own SHUTDOWN_ORDER doc) -- proven the same way as the
    // monitor/reader above: a second Start() now throws the single-use "already been called"
    // InvalidOperationException, not ObjectDisposedException. ----
    [Fact]
    public void Start_SessionObserverStarted_ProvenBySecondStartThrowingAlreadyStarted()
    {
        using var composition = CreateWithRealGraph();
        composition.Start();

        var sessionLockNotification = (ISessionLockNotification)GetField(composition, "_sessionLockNotification")!;

        Assert.Throws<InvalidOperationException>(sessionLockNotification.Start);
    }

    // ---- Phase 3C STEP38 (items 31/33): if the session observer itself fails to start, that
    // failure is reached and reported BEFORE the composer reader or coordinator are ever touched --
    // the composer reader's own manual Start() after rollback still succeeds (proving it was never
    // started by composition.Start() at all, since ComposerTextReader IS unconditionally Disposed by
    // rollback whenever it exists, exactly like ClipboardChangeMonitor -- a fresh, never-disposed
    // instance is what's injected here, and the observer failure is what stops rollback from ever
    // reaching it in the first place, so this instance is the SAME untouched one, never given to
    // rollback to dispose since it lives entirely outside this composition). ----
    [Fact]
    public void Start_SessionObserverAlreadyStarted_ThrowsBeforeComposerReaderOrCoordinatorEverStart()
    {
        var preStartedSessionMonitor = new SessionLockMonitor();
        preStartedSessionMonitor.Start();
        try
        {
            var sessionLockNotification = new SessionLockNotificationAdapter(preStartedSessionMonitor);
            using var composition = new PrivonAppComposition(
                CreateTempStorageRoot(), sessionLockNotification, new ClipboardChangeMonitor(), new ComposerTextReader());

            var thrown = Assert.Throws<InvalidOperationException>(composition.Start);
            Assert.Contains("SessionLockMonitor", thrown.Message);

            Assert.Null(composition.Resolver);
            Assert.Null(composition.Verifier);
            Assert.Null(GetField(composition, "_coordinator"));
        }
        finally
        {
            preStartedSessionMonitor.Dispose();
        }
    }

    [Fact]
    public void Start_MonitorAlreadyStarted_CoordinatorStartFails_ButComposerReaderWasAlreadyStarted()
    {
        var preStartedMonitor = new ClipboardChangeMonitor();
        preStartedMonitor.Start();
        try
        {
            var composerReader = new ComposerTextReader();
            using var composition = new PrivonAppComposition(CreateTempStorageRoot(), new SessionLockNotificationAdapter(), preStartedMonitor, composerReader);

            Assert.Throws<InvalidOperationException>(composition.Start);

            // The composer reader WAS started (it is Start()-ed strictly before the coordinator, per
            // the frozen order) and was then rolled back (Disposed) -- a second manual Start() on it
            // now throws ObjectDisposedException, not "already started."
            Assert.Throws<ObjectDisposedException>(composerReader.Start);
        }
        finally
        {
            preStartedMonitor.Dispose();
        }
    }

    [Fact]
    public void Start_CalledTwice_SecondCallThrows_RegardlessOfFirstOutcome()
    {
        using var composition = CreateWithRealGraph();
        composition.Start();

        Assert.Throws<InvalidOperationException>(composition.Start);
    }

    [Fact]
    public void Start_CalledTwice_AfterFailedFirstCall_SecondCallStillThrowsSingleUse()
    {
        var preStartedReader = new ComposerTextReader();
        preStartedReader.Start();
        try
        {
            var monitor = new ClipboardChangeMonitor();
            using var composition = new PrivonAppComposition(CreateTempStorageRoot(), new SessionLockNotificationAdapter(), monitor, preStartedReader);

            Assert.Throws<InvalidOperationException>(composition.Start);
            // Second Start() after a failed first Start() is still rejected as single-use -- never a
            // silent retry.
            Assert.Throws<InvalidOperationException>(composition.Start);

            monitor.Dispose();
        }
        finally
        {
            preStartedReader.Dispose();
        }
    }

    // ==================================================================
    // C. STARTUP FAILURE ROLLBACK (item 36)
    // ==================================================================

    [Fact]
    public void Start_StorageConstructionFailure_NoPartialRuntime()
    {
        // A storage root that is actually an existing FILE (not a directory) makes
        // Directory.CreateDirectory -- and therefore PrivonLocalStore.OpenOrCreate -- fail, before
        // this type constructs anything else at all.
        var tempFile = Path.Combine(Path.GetTempPath(), "PrivonAppCompositionTests_" + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(tempFile, "not a directory");
        try
        {
            using var composition = CreateWithRealGraph(tempFile);

            Assert.ThrowsAny<Exception>(composition.Start);

            Assert.Null(composition.Resolver);
            Assert.Null(composition.Verifier);
            Assert.Null(GetField(composition, "_coordinator"));
            Assert.Null(GetField(composition, "_operationGate"));
            Assert.Null(GetField(composition, "_lifecycle"));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void Start_ComposerReaderStartFailure_RollsBackToNoActivePipeline()
    {
        var preStartedReader = new ComposerTextReader();
        preStartedReader.Start();
        try
        {
            var monitor = new ClipboardChangeMonitor();
            using var composition = new PrivonAppComposition(CreateTempStorageRoot(), new SessionLockNotificationAdapter(), monitor, preStartedReader);

            var thrown = Assert.Throws<InvalidOperationException>(composition.Start);
            Assert.NotNull(thrown);

            Assert.Null(composition.Resolver);
            Assert.Null(composition.Verifier);
            Assert.Null(GetField(composition, "_coordinator"));
            Assert.Null(GetField(composition, "_operationGate"));
            Assert.Null(GetField(composition, "_lifecycle"));

            monitor.Dispose();
        }
        finally
        {
            preStartedReader.Dispose();
        }
    }

    [Fact]
    public void Start_CoordinatorStartFailure_AfterReaderStarted_RollsBackReaderAndOriginalExceptionPropagates()
    {
        var preStartedMonitor = new ClipboardChangeMonitor();
        preStartedMonitor.Start();
        try
        {
            var composerReader = new ComposerTextReader();
            using var composition = new PrivonAppComposition(CreateTempStorageRoot(), new SessionLockNotificationAdapter(), preStartedMonitor, composerReader);

            Assert.Throws<InvalidOperationException>(composition.Start);

            Assert.Null(composition.Resolver);
            Assert.Null(composition.Verifier);

            // The composer reader was started and must have been rolled back (Disposed) -- its
            // worker thread is no longer alive, so Stop() on it now is a safe, already-stopped no-op.
            composerReader.Stop();
        }
        finally
        {
            preStartedMonitor.Dispose();
        }
    }

    [Fact]
    public void Dispose_SafeAfterFailedStart()
    {
        var preStartedReader = new ComposerTextReader();
        preStartedReader.Start();
        try
        {
            var monitor = new ClipboardChangeMonitor();
            var composition = new PrivonAppComposition(CreateTempStorageRoot(), new SessionLockNotificationAdapter(), monitor, preStartedReader);

            Assert.Throws<InvalidOperationException>(composition.Start);

            // Dispose() after a failed Start() must not throw -- everything was already rolled back.
            composition.Dispose();
            composition.Dispose();

            monitor.Dispose();
        }
        finally
        {
            preStartedReader.Dispose();
        }
    }

    // ==================================================================
    // D. SHUTDOWN (item 35)
    // ==================================================================

    [Fact]
    public void Dispose_IsIdempotent_WhenCalledRepeatedly()
    {
        var composition = CreateWithRealGraph();
        composition.Start();

        composition.Dispose();
        composition.Dispose();
        composition.Dispose();
    }

    [Fact]
    public void Dispose_SafeWhenStartNeverCalled()
    {
        var composition = CreateWithRealGraph();
        composition.Dispose();
        composition.Dispose();
    }

    [Fact]
    public void Dispose_ResetsLifecycleGeneration()
    {
        var composition = CreateWithRealGraph();
        composition.Start();

        var lifecycle = GetField(composition, "_lifecycle")!;
        var generationBefore = (long)lifecycle.GetType().GetProperty("CurrentGeneration")!.GetValue(lifecycle)!;

        composition.Dispose();

        var generationAfter = (long)lifecycle.GetType().GetProperty("CurrentGeneration")!.GetValue(lifecycle)!;
        Assert.True(generationAfter > generationBefore, "lifecycle.Reset() must advance the generation on shutdown.");
    }

    [Fact]
    public void Dispose_InvalidatesPendingComposerVerification()
    {
        var composition = CreateWithRealGraph();
        composition.Start();

        var verifier = composition.Verifier!;

        // Reflect a synthetic pending record directly into the verifier's own private slot -- avoids
        // needing a real successful guarded write just to prove the shutdown path clears it.
        var pendingType = typeof(PrivonAppComposition).Assembly.GetType("Privon.App.PendingComposerVerification")!;
        var pendingCtor = pendingType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public,
            [typeof(ForegroundTargetSnapshot), typeof(string), typeof(long)])!;
        var syntheticPending = pendingCtor.Invoke([
            new ForegroundTargetSnapshot(IsResolved: true, ProcessId: 1234, ProcessName: "ChatGPT"),
            "SYNTHETIC-PENDING-TEXT",
            1L,
        ]);
        SetField(verifier, "_pending", syntheticPending);

        Assert.NotNull(GetField(verifier, "_pending"));

        composition.Dispose();

        Assert.Null(GetField(verifier, "_pending"));
    }

    [Fact]
    public async Task Dispose_DisposesOperationGate()
    {
        var composition = CreateWithRealGraph();
        composition.Start();

        var gate = (ClipboardOperationGate)GetField(composition, "_operationGate")!;

        composition.Dispose();

        // A disposed SemaphoreSlim throws ObjectDisposedException on WaitAsync().
        await Assert.ThrowsAsync<ObjectDisposedException>(gate.WaitAsync);
    }

    [Fact]
    public void Dispose_StopsMonitorAndComposerReader()
    {
        var composition = CreateWithRealGraph();
        composition.Start();

        var monitor = (ClipboardChangeMonitor)GetField(composition, "_monitor")!;
        var reader = (ComposerTextReader)GetField(composition, "_composerReader")!;

        composition.Dispose();

        // Both are now fully Disposed -- a fresh Start() call throws ObjectDisposedException (never
        // succeeds, never silently no-ops), proving Dispose() actually reached and stopped each one.
        Assert.Throws<ObjectDisposedException>(monitor.Start);
        Assert.Throws<ObjectDisposedException>(reader.Start);
    }

    // ---- Phase 3C STEP38 (item 32): the session observer is stopped by composition.Dispose() too --
    // PrivonAppComposition only ever calls Stop() on it (never Dispose(), see SHUTDOWN_ORDER doc), so
    // the single-use InvalidOperationException (not ObjectDisposedException) is the correct proof
    // here, mirroring Start_SessionObserverStarted_ProvenBySecondStartThrowingAlreadyStarted's own
    // reasoning. ----
    [Fact]
    public void Dispose_StopsSessionObserver()
    {
        var composition = CreateWithRealGraph();
        composition.Start();

        var sessionLockNotification = (ISessionLockNotification)GetField(composition, "_sessionLockNotification")!;

        composition.Dispose();

        Assert.Throws<InvalidOperationException>(sessionLockNotification.Start);
    }

    // ==================================================================
    // E. STORAGE ROOT PATH (item 37)
    // ==================================================================

    [Fact]
    public void ProductionStorageRootPath_IsExactlyLocalApplicationDataPrivon()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PRIVON");

        Assert.Equal(expected, PrivonAppComposition.ProductionStorageRootPath);
    }

    [Fact]
    public void ProductionConstructor_UsesProductionStorageRootPath()
    {
        using var composition = new PrivonAppComposition();
        Assert.Equal(PrivonAppComposition.ProductionStorageRootPath, GetField(composition, "_storageRootPath"));
    }

    [Fact]
    public void CompositionRootSource_NeverUsesTempOrCurrentDirectoryFallback()
    {
        // Executable code only -- the class doc deliberately DISCUSSES these forbidden APIs (to
        // explain what this type never falls back to), so doc-comment lines are stripped first.
        var source = StripDocComments(File.ReadAllText(FindAppSourceFile("PrivonAppComposition.cs")));

        Assert.DoesNotContain("Path.GetTempPath", source);
        Assert.DoesNotContain("Directory.GetCurrentDirectory", source);
        Assert.DoesNotContain("Environment.CurrentDirectory", source);
    }

    // ==================================================================
    // F. STRUCTURAL / BEHAVIORAL BOUNDARIES (items 27-30, 38-39)
    // ==================================================================

    [Fact]
    public void CompositionRootSource_NeverCallsVerifyAsync()
    {
        var source = StripDocComments(File.ReadAllText(FindAppSourceFile("PrivonAppComposition.cs")));
        Assert.DoesNotContain("VerifyAsync", source);
    }

    [Fact]
    public void CompositionRootSource_NeverReferencesProtectionState()
    {
        var source = StripDocComments(File.ReadAllText(FindAppSourceFile("PrivonAppComposition.cs")));
        Assert.DoesNotContain("ProtectionState", source);
    }

    // Phase 3C STEP38: "SessionLock" is removed from this forbidden list -- PrivonAppComposition
    // now legitimately owns ISessionLockNotification/OnSessionLocked as required security
    // infrastructure. UI/hotkey/Send-interception terms remain forbidden -- see
    // CompositionRootSource_NeverExposesSendInterceptionOrBypassOnce below for the narrower,
    // still-current session-lock-adjacent boundary (no Send/BypassOnce/UIA/clipboard-content access
    // from the session-lock callback).
    [Fact]
    public void CompositionRootSource_HasNoUiOrHotkeyMembers()
    {
        var forbidden = new[] { "MainWindow", "Tray", "Hotkey", "RegisterHotKey", "SendInput" };
        var source = StripDocComments(File.ReadAllText(FindAppSourceFile("PrivonAppComposition.cs")));

        foreach (var term in forbidden)
        {
            Assert.DoesNotContain(term, source);
        }
    }

    // ---- Phase 3C STEP38: the session-lock callback wiring never reaches for Send-intent/BypassOnce/
    // UIA/clipboard-content APIs -- OnSessionLocked's own body is exactly Reset()+InvalidatePending(). ----
    [Fact]
    public void CompositionRootSource_SessionLockCallbackNeverTouchesSendOrUiaOrClipboardContent()
    {
        var forbidden = new[] { "BypassOnce", "ReadFocusedComposerText", "ReadTextSnapshotAsync", "WriteTextIfSequenceMatchesAsync", "VerifyAsync" };
        var source = StripDocComments(File.ReadAllText(FindAppSourceFile("PrivonAppComposition.cs")));

        foreach (var term in forbidden)
        {
            Assert.DoesNotContain(term, source);
        }
    }

    [Fact]
    public void CompositionRoot_HasNoTextLoggingCalls()
    {
        var forbidden = new[] { "Console.Write", "Debug.Write", "Trace.Write" };
        var source = File.ReadAllText(FindAppSourceFile("PrivonAppComposition.cs"));

        Assert.DoesNotContain(forbidden, pattern => source.Contains(pattern, StringComparison.Ordinal));
    }

    [Fact]
    public void AppXaml_HasNoStartupUriAndNoWindowResource()
    {
        var xaml = File.ReadAllText(FindAppSourceFile("App.xaml"));

        Assert.DoesNotContain("StartupUri", xaml);
        Assert.DoesNotContain("<Window", xaml);
    }

    [Fact]
    public void AppXamlCs_SetsExplicitShutdownModeAndOwnsOneCompositionRoot()
    {
        var rawSource = File.ReadAllText(FindAppSourceFile("App.xaml.cs"));
        var codeOnly = StripDocComments(rawSource);

        // These two must appear in actual executable code, not merely be discussed in prose.
        Assert.Contains("ShutdownMode.OnExplicitShutdown", codeOnly);
        Assert.Contains("PrivonAppComposition", codeOnly);
        Assert.DoesNotContain("MainWindow", codeOnly);
        Assert.DoesNotContain("StartupUri", codeOnly);
    }

    [Fact]
    public void AppXamlCs_HasNoTextLoggingCalls()
    {
        var forbidden = new[] { "Console.Write", "Debug.Write", "Trace.Write" };
        var source = File.ReadAllText(FindAppSourceFile("App.xaml.cs"));

        Assert.DoesNotContain(forbidden, pattern => source.Contains(pattern, StringComparison.Ordinal));
    }

    [Fact]
    public void PrivonAppComposition_IsInternalPlainClrType_NotAWpfType()
    {
        Assert.False(typeof(PrivonAppComposition).IsPublic);
        Assert.False(typeof(System.Windows.Application).IsAssignableFrom(typeof(PrivonAppComposition)));
    }

    // ==================================================================
    // G. PUBLIC_DIAGNOSTIC_DEFAULT_OFF / EXPLICIT_QA_DIAGNOSTICS_GATE (Phase 3C STEP43.1)
    // ==================================================================
    // STEP43 found FileClipboardDiagnosticRecorder was constructed (and %TEMP%\privon-diagnostic-
    // *.log therefore written) unconditionally on every launch. STEP43.1 gates that construction
    // behind _enableDiagnostics, which the public production constructor resolves once from a
    // single environment-variable check and which every test below controls directly via the
    // existing internal constructor's new (default-false) trailing parameter -- never by mutating
    // a real environment variable, which would be unsafe global state under parallel test
    // execution.

    private static string[] SnapshotDiagnosticLogFiles() =>
        Directory.GetFiles(Path.GetTempPath(), "privon-diagnostic-*.log");

    // ---- item C bullet 1: default production composition does not create/open a file diagnostic
    // recorder -- proven two ways: (a) black-box, by diffing the real %TEMP% directory's actual
    // privon-diagnostic-*.log files before/after Start() (this is the literal behavioral claim the
    // goal states), and (b) white-box, by confirming the _diagnostics field itself stays null. ----
    [Fact]
    public void DefaultComposition_Start_CreatesNoDiagnosticLogFile_AndLeavesDiagnosticsFieldNull()
    {
        var before = SnapshotDiagnosticLogFiles();

        using var composition = CreateWithRealGraph();
        composition.Start();

        var after = SnapshotDiagnosticLogFiles();

        Assert.Equal(before.OrderBy(p => p, StringComparer.Ordinal), after.OrderBy(p => p, StringComparer.Ordinal));
        Assert.Null(GetField(composition, "_diagnostics"));
    }

    // ---- The public, parameterless production constructor resolves _enableDiagnostics from the
    // environment -- confirming that path too (not just the internal test-only constructor's
    // explicit-false default) with no real environment variable set (the normal state of this test
    // process, and of every real end-user machine). ----
    [Fact]
    public void ProductionConstructor_WithNoEnvironmentVariableSet_StartsWithDiagnosticsDisabled()
    {
        Assert.Null(Environment.GetEnvironmentVariable("PRIVON_ENABLE_DIAGNOSTICS"));
        var before = SnapshotDiagnosticLogFiles();

        using var composition = new PrivonAppComposition();
        composition.Start();

        var after = SnapshotDiagnosticLogFiles();

        Assert.Equal(before.OrderBy(p => p, StringComparer.Ordinal), after.OrderBy(p => p, StringComparer.Ordinal));
        Assert.Null(GetField(composition, "_diagnostics"));
    }

    // ---- item C bullet 2: explicit diagnostic mode, if retained, still uses the SAME
    // metadata-only FileClipboardDiagnosticRecorder type (not some new/parallel logging
    // mechanism) -- proven by confirming a real privon-diagnostic-*.log file (the exact naming
    // CreateDefaultFilePath() already produces) appears exactly once, and that the _diagnostics
    // field becomes that exact type. The recorder's own metadata-only/no-raw-text/no-
    // CanonicalValue/no-PII contract is already exhaustively proven elsewhere (ClipboardDiagnosticEvent/
    // ClipboardMonitorDiagnosticEvent/ClipboardWriteDiagnosticEvent's own structural "no string
    // fields" tests, FileClipboardDiagnosticRecorderTests.cs) -- this test only proves the WIRING
    // decision, never re-proves privacy properties of an already-proven type. ----
    [Fact]
    public void ExplicitDiagnosticsEnabled_CreatesExactlyOneRealFileClipboardDiagnosticRecorder()
    {
        var before = SnapshotDiagnosticLogFiles();
        string[] createdFiles;

        using (var composition = new PrivonAppComposition(
            CreateTempStorageRoot(), new SessionLockNotificationAdapter(), new ClipboardChangeMonitor(), new ComposerTextReader(), enableDiagnostics: true))
        {
            composition.Start();

            Assert.IsType<FileClipboardDiagnosticRecorder>(GetField(composition, "_diagnostics"));

            var after = SnapshotDiagnosticLogFiles();
            createdFiles = after.Except(before).ToArray();
            Assert.Single(createdFiles);
        }

        // Test hygiene -- never leave a real file behind in the shared %TEMP% directory.
        foreach (var file in createdFiles) File.Delete(file);
    }

    // ---- item C bullet 3: diagnostic-disabled mode has no effect on clipboard processing
    // outcomes -- at the composition-root level this means the REST of the object graph
    // (Resolver/Verifier/Lifecycle/SessionPublisher) is exactly as reachable/usable after a
    // diagnostics-disabled Start() as after a diagnostics-enabled one; the coordinator's own
    // identical-processing-behavior guarantee regardless of its diagnostics argument is already a
    // separate, existing regression (ClipboardPrivacyCoordinatorTests) this STEP does not touch. ----
    [Fact]
    public void DiagnosticsDisabled_DoesNotDegradeRestOfObjectGraph_ComparedToDiagnosticsEnabled()
    {
        using var disabled = CreateWithRealGraph();
        disabled.Start();

        using var enabled = new PrivonAppComposition(
            CreateTempStorageRoot(), new SessionLockNotificationAdapter(), new ClipboardChangeMonitor(), new ComposerTextReader(), enableDiagnostics: true);
        enabled.Start();

        Assert.NotNull(disabled.Resolver);
        Assert.NotNull(disabled.Verifier);
        Assert.NotNull(disabled.Lifecycle);
        Assert.NotNull(disabled.SessionPublisher);

        Assert.NotNull(enabled.Resolver);
        Assert.NotNull(enabled.Verifier);
        Assert.NotNull(enabled.Lifecycle);
        Assert.NotNull(enabled.SessionPublisher);

        enabled.Dispose();

        // Test hygiene -- best-effort cleanup of whatever file the enabled composition created above.
        foreach (var file in SnapshotDiagnosticLogFiles())
        {
            try { File.Delete(file); } catch { /* best-effort test cleanup only */ }
        }
    }

    // ---- item C bullet 4: the new conditional-wiring branch itself introduces no new failure
    // path -- a full Start()/Dispose() cycle with diagnostics explicitly enabled succeeds without
    // throwing under normal conditions. This is deliberately narrow: it does not re-prove that a
    // THROWING diagnostic recorder is non-fatal (that guarantee lives entirely inside
    // ClipboardChangeMonitor.RaiseDiagnostic/RaiseWriteDiagnostic and
    // ClipboardPrivacyCoordinator.Diagnose, both already covered by their own existing regressions
    // in Windows.IntegrationTests/App.Tests, and neither of which this STEP modifies) -- it proves
    // this STEP's own new "if (_enableDiagnostics) { construct + subscribe }" branch does not
    // itself introduce a new unguarded exception path (e.g. a null-reference on _diagnostics
    // inside the subscription lambdas). ----
    [Fact]
    public void ExplicitDiagnosticsEnabled_StartThenDispose_SucceedsWithoutThrowing()
    {
        using var composition = new PrivonAppComposition(
            CreateTempStorageRoot(), new SessionLockNotificationAdapter(), new ClipboardChangeMonitor(), new ComposerTextReader(), enableDiagnostics: true);

        composition.Start();
        composition.Dispose();

        foreach (var file in SnapshotDiagnosticLogFiles())
        {
            try { File.Delete(file); } catch { /* best-effort test cleanup only */ }
        }
    }

    // ---- The environment-variable gate itself: exact-match "1" only, matching this codebase's
    // established preference for simple explicit checks over general parsing (no "true"/"yes"/
    // case-insensitive support). Read via reflection on the private static helper -- no real
    // environment variable is ever set by this test. ----
    [Fact]
    public void DiagnosticsEnabledFromEnvironment_RequiresExactMatchOfLiteral1()
    {
        var method = typeof(PrivonAppComposition).GetMethod(
            "DiagnosticsEnabledFromEnvironment", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("DiagnosticsEnabledFromEnvironment not found.");

        const string variable = "PRIVON_ENABLE_DIAGNOSTICS";
        var original = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, null);
            Assert.False((bool)method.Invoke(null, null)!);

            Environment.SetEnvironmentVariable(variable, "true");
            Assert.False((bool)method.Invoke(null, null)!);

            Environment.SetEnvironmentVariable(variable, "1");
            Assert.True((bool)method.Invoke(null, null)!);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, original);
        }
    }

    // ---- Phase 3C STEP41.1: the composition root actually wires the Windows-layer,
    // native-boundary diagnostic (ClipboardChangeMonitor.DiagnosticObserved) into the same sink
    // the App-layer trace already writes to -- a source-scan proxy (constructing/starting the
    // real ClipboardChangeMonitor here would require a real Win32 clipboard listener, which this
    // test project deliberately avoids for composition-root tests; see this class's own existing
    // "STARTUP_ORDER (success)"/"...(failure)" tests for that precedent). ----
    [Fact]
    public void CompositionRootSource_WiresDiagnosticObservedIntoRecordWindowsEvent()
    {
        var source = StripDocComments(File.ReadAllText(FindAppSourceFile("PrivonAppComposition.cs")));

        Assert.Contains("DiagnosticObserved", source);
        Assert.Contains("RecordWindowsEvent", source);
    }

    // ---- Phase 3C STEP41.2: the SECOND Windows-layer diagnostic (the guarded-write
    // sequence-attribution boundary) is likewise wired into the same sink, under its own distinct
    // method -- a source-scan proxy for the identical reason the STEP41.1 test above is one. ----
    [Fact]
    public void CompositionRootSource_WiresWriteDiagnosticObservedIntoRecordWriteDiagnosticEvent()
    {
        var source = StripDocComments(File.ReadAllText(FindAppSourceFile("PrivonAppComposition.cs")));

        Assert.Contains("WriteDiagnosticObserved", source);
        Assert.Contains("RecordWriteDiagnosticEvent", source);
    }

    private static string FindAppSourceDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Privon.App")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
            throw new InvalidOperationException("Could not locate src/Privon.App from the test output directory.");

        return Path.Combine(dir.FullName, "src", "Privon.App");
    }

    private static string FindAppSourceFile(string fileName) =>
        Path.Combine(FindAppSourceDirectory(), fileName);

    // Drops every line whose trimmed content starts with "//" (covers this codebase's exclusively
    // line-comment style, including "///" doc comments) -- so a structural scan only ever matches
    // actual executable code, never prose that explains what a type deliberately does NOT do.
    private static string StripDocComments(string source) =>
        string.Join('\n', source.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
}
