using Privon.App;
using Privon.Core;
using Privon.Detection;
using Privon.Detection.Detectors;
using Privon.Detection.Scope;
using Privon.Storage;
using Privon.UsProductIntegration;
using Privon.Windows;

namespace Privon.App.Tests;

// SYNTHETIC DATA ONLY. The protection status shown to the user through the EXISTING tray path (ITrayIconSurface owner
// PrivonAppUiBridge + IDispatcherScheduler). The WinForms NotifyIcon itself needs a live STA thread and is not run here: the
// real tray surface is covered only by source-structure checks, and real rendering is manual release QA.
//
// This is a status DISPLAY. The product cannot block a paste or a send, and nothing here says it did.
public sealed class ProtectionStatusDisplayTests : IDisposable
{
    // The sentence the instruction requires, written out independently of the implementation.
    private const string RequiredWarning =
        "Protection of personal information could not be confirmed. The original text may remain on the clipboard, and PRIVON does not currently block sending.";

    private readonly List<string> _roots = [];
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
        foreach (var r in _roots)
        {
            try { Directory.Delete(r, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private static ClipboardProtectionStatus Status(ProtectionState state, bool unresolved = false) =>
        new(state, 1, unresolved, 1, unresolved ? "X" : null);

    private static ClipboardDiagnosticEvent Receive(long id) => ClipboardDiagnosticEvent.AppClipboardChangeReceived(id, (uint)id, true, true);

    private static ClipboardDiagnosticEvent Terminal(long id, ClipboardDiagnosticTerminalReason reason) =>
        ClipboardDiagnosticEvent.AttemptTerminal(id, reason, reason == ClipboardDiagnosticTerminalReason.ProcessingException ? "SyntheticException" : null);

    private sealed class PlainTray : ITrayIconSurface
    {
        public event EventHandler? ExitRequested { add { } remove { } }
        public event EventHandler? AutoStartToggleRequested { add { } remove { } }
        public event EventHandler? SettingsRequested { add { } remove { } }
        public event EventHandler? ChromeReconnectNotificationClicked { add { } remove { } }
        public void Show() { }
        public void SetAutoStartChecked(bool isChecked) { }
        public void ShowChromeReconnectNotification() { }
        public void Dispose() { }
    }

    private (PrivonAppUiBridge Bridge, FakeDispatcherScheduler Scheduler) NewBridge(
        ITrayIconSurface tray, ClipboardProtectionStatusTracker? tracker, bool synchronous = true)
    {
        string root = Path.Combine(Path.GetTempPath(), "ProtectionStatusDisplay", Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        var store = PrivonLocalStore.OpenOrCreate(root);
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var scheduler = new FakeDispatcherScheduler { RunSynchronously = synchronous };
        var bridge = new PrivonAppUiBridge(
            new ClipboardDecisionSessionPublisher(lifecycle), lifecycle, new FakeClipboardDecisionResolver(), scheduler, tray,
            () => new FakeDecisionPromptSurface(),
            new WindowsAutoStartCoordinator(new FakeWindowsAutoStartRegistration(), () => "C:\\PRIVON\\PRIVON.exe"),
            new ProtectionCategorySettingsService(store), new UserExceptionService(store), DetectionPipeline.CreateDefault(),
            () => store.IsMasterKeyUnavailable, () => new FakeSettingsSurface(),
            new NativeMessagingHostRegistrationCoordinator(new FakeNativeMessagingHostRegistrationEnvironment()),
            protectionStatus: tracker);
        _disposables.Add(bridge);
        return (bridge, scheduler);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not met within the timeout.");
            await Task.Delay(5);
        }
    }

    // ================================================================================================================
    // What each state says
    // ================================================================================================================

    [Theory]
    [InlineData(ProtectionState.Error)]
    [InlineData(ProtectionState.Limited)]
    public void ErrorAndLimited_AreWarnings_ThatSayTheProtectionIsUnconfirmed_TheTextMayRemain_AndNothingIsBlocked(ProtectionState state)
    {
        var p = ProtectionStatusPresenter.Present(Status(state, unresolved: true));

        Assert.True(p.IsWarning);
        Assert.Contains("original text may remain on the clipboard", p.Detail);
        Assert.Contains("PRIVON does not currently block sending", p.Detail);
        Assert.Contains("could not be", p.Detail);
    }

    [Fact]
    public void ErrorShowsTheRequiredSentenceVerbatim()
    {
        Assert.Equal(RequiredWarning, ProtectionStatusPresenter.Present(Status(ProtectionState.Error, unresolved: true)).Detail);
    }

    [Fact]
    public void VerifiedIsScopedToTheLastProcessedAttempt_NeverToTheCurrentInput_OrToTransmissionSafety()
    {
        var p = ProtectionStatusPresenter.Present(Status(ProtectionState.Verified));

        Assert.False(p.IsWarning);
        Assert.Contains("Last rewrite", p.Tooltip);
        Assert.Contains("last processing attempt", p.Detail);
        Assert.Contains("does not guarantee", p.Detail);
        Assert.Contains("does not currently block sending", p.Detail);
    }

    [Fact]
    public void NeedsDecisionAndScanning_AreNotProtected_AndSayTheTextMayStillBeThere()
    {
        var decision = ProtectionStatusPresenter.Present(Status(ProtectionState.NeedsDecision));
        var scanning = ProtectionStatusPresenter.Present(Status(ProtectionState.Scanning));

        Assert.Contains("decision", decision.Tooltip);
        Assert.Contains("not yet protected", decision.Tooltip);
        Assert.Contains("original", decision.Detail);
        Assert.Contains("not yet protected", scanning.Tooltip);
        Assert.False(decision.IsWarning);
        Assert.False(scanning.IsWarning);
    }

    [Fact]
    public void ReadyDoesNotGuaranteeTransmissionSafety()
    {
        var p = ProtectionStatusPresenter.Present(Status(ProtectionState.Ready));

        Assert.Contains("not guaranteed", p.Tooltip);
        Assert.Contains("does not guarantee", p.Detail);
        Assert.False(p.IsWarning);
    }

    [Fact]
    public void AScanInProgressStillShowsAnUnresolvedEarlierFailure_AsAWarning()
    {
        var p = ProtectionStatusPresenter.Present(Status(ProtectionState.Scanning, unresolved: true));

        Assert.True(p.IsWarning);
        Assert.Contains("earlier error", p.Tooltip);
    }

    [Fact]
    public void EveryStateAndFlagFitsTheNotifyIconLimit_AndNothingEverClaimsATransmissionWasBlocked()
    {
        var banned = new[] { "차단했", "was blocked", "차단되었", "차단 완료", "막았", "blocked", "Blocked" };

        foreach (var state in Enum.GetValues<ProtectionState>())
        {
            foreach (bool unresolved in new[] { false, true })
            {
                var p = ProtectionStatusPresenter.Present(Status(state, unresolved));

                Assert.True(p.Tooltip.Length <= 63, $"{state}/{unresolved} tooltip is {p.Tooltip.Length} chars");
                Assert.StartsWith("PRIVON", p.Tooltip);
                Assert.DoesNotContain(banned, b => p.Tooltip.Contains(b) || p.Detail.Contains(b));
                Assert.False(string.IsNullOrWhiteSpace(p.Detail));
            }
        }
    }

    [Theory]
    [InlineData(ProtectionState.Blocked)]
    [InlineData(ProtectionState.Paused)]
    [InlineData(ProtectionState.Initializing)]
    public void AStateTheTrackerNeverProduces_IsShownAsUnconfirmed_NotAsSafeOrBlocked(ProtectionState state)
    {
        var p = ProtectionStatusPresenter.Present(Status(state));

        Assert.True(p.IsWarning);
        Assert.Contains("could not be", p.Detail);
    }

    [Fact]
    public void ANotificationIsRequestedOnlyWhenAWarningBegins_OrEscalatesToError()
    {
        ProtectionStatusPresentation P(ProtectionState s, bool u = false) => ProtectionStatusPresenter.Present(Status(s, u));
        var error = P(ProtectionState.Error, true);
        var limited = P(ProtectionState.Limited, true);
        var scanningWithFailure = P(ProtectionState.Scanning, true);
        var verified = P(ProtectionState.Verified);
        var ready = P(ProtectionState.Ready);

        Assert.True(ProtectionStatusPresenter.ShouldNotify(null, error));
        Assert.True(ProtectionStatusPresenter.ShouldNotify(verified, error));
        Assert.True(ProtectionStatusPresenter.ShouldNotify(ready, limited));
        Assert.True(ProtectionStatusPresenter.ShouldNotify(limited, error));      // escalation
        Assert.False(ProtectionStatusPresenter.ShouldNotify(error, error));       // repeated
        Assert.False(ProtectionStatusPresenter.ShouldNotify(error, scanningWithFailure));
        Assert.False(ProtectionStatusPresenter.ShouldNotify(scanningWithFailure, error)); // the same failure episode
        Assert.False(ProtectionStatusPresenter.ShouldNotify(error, limited));     // de-escalation
        Assert.False(ProtectionStatusPresenter.ShouldNotify(null, verified));
        Assert.False(ProtectionStatusPresenter.ShouldNotify(error, verified));    // recovery shows, but does not alert
    }

    // ================================================================================================================
    // The consumer: PrivonAppUiBridge -> the tray
    // ================================================================================================================

    [Fact]
    public void WithTheTracker_StartRendersTheInitialStatus_AndEveryChangeReachesTheTray()
    {
        var tracker = new ClipboardProtectionStatusTracker(scanWatchdog: Timeout.InfiniteTimeSpan);
        var tray = new FakeTrayIconSurface();
        var (bridge, _) = NewBridge(tray, tracker);

        bridge.Start();
        Assert.Equal(ProtectionState.Ready, Assert.Single(tray.ProtectionStatusHistory).Presentation.State);

        tracker.Record(Receive(1));
        tracker.Record(Terminal(1, ClipboardDiagnosticTerminalReason.Success));

        Assert.Equal(ProtectionState.Verified, tray.ProtectionStatusHistory[^1].Presentation.State);
        Assert.All(tray.ProtectionStatusHistory, h => Assert.False(h.Notify));
    }

    [Fact]
    public void AFailureNotifiesOnce_RepeatedFailuresDoNotRepeatTheAlert_AndARecoveryUpdatesTheDisplay()
    {
        var tracker = new ClipboardProtectionStatusTracker(scanWatchdog: Timeout.InfiniteTimeSpan);
        var tray = new FakeTrayIconSurface();
        var (bridge, _) = NewBridge(tray, tracker);
        bridge.Start();

        void Fail(long id)
        {
            tracker.Record(Receive(id));
            tracker.Record(Terminal(id, ClipboardDiagnosticTerminalReason.ProcessingException));
        }

        Fail(1);
        Fail(2);
        Fail(3);
        int alertsInFirstEpisode = tray.ProtectionStatusHistory.Count(h => h.Notify);

        tracker.Record(Receive(4));
        tracker.Record(Terminal(4, ClipboardDiagnosticTerminalReason.Success));
        var recovered = tray.ProtectionStatusHistory[^1];

        Fail(5);
        int alertsAfterSecondEpisode = tray.ProtectionStatusHistory.Count(h => h.Notify);

        Assert.Equal(1, alertsInFirstEpisode);
        Assert.Equal(ProtectionState.Verified, recovered.Presentation.State);
        Assert.False(recovered.Presentation.IsWarning);
        Assert.False(recovered.Notify);
        Assert.Equal(2, alertsAfterSecondEpisode); // a NEW failure after a real recovery alerts again
        Assert.Equal(RequiredWarning, tray.ProtectionStatusHistory.First(h => h.Notify).Presentation.Detail);
    }

    [Fact]
    public void WithoutTheTracker_OrWithATrayThatCannotShowIt_NothingChanges_AndNothingThrows()
    {
        var tray = new FakeTrayIconSurface();
        var (shipped, _) = NewBridge(tray, tracker: null);
        shipped.Start();
        Assert.Empty(tray.ProtectionStatusHistory);

        var tracker = new ClipboardProtectionStatusTracker(scanWatchdog: Timeout.InfiniteTimeSpan);
        var (plain, _) = NewBridge(new PlainTray(), tracker);
        plain.Start();
        tracker.Record(Receive(1));
        tracker.Record(Terminal(1, ClipboardDiagnosticTerminalReason.ProcessingException));
    }

    [Fact]
    public void TheDisplayIsPostedToTheUiScheduler_AndAlwaysShowsTheLatestStatusWhenItRuns()
    {
        var tracker = new ClipboardProtectionStatusTracker(scanWatchdog: Timeout.InfiniteTimeSpan);
        var tray = new FakeTrayIconSurface();
        var (bridge, scheduler) = NewBridge(tray, tracker, synchronous: false);
        bridge.Start();
        Assert.Empty(tray.ProtectionStatusHistory);           // nothing touches the tray off the UI thread
        scheduler.RunAllPending();
        tray.ProtectionStatusHistory.Clear();

        tracker.Record(Receive(1));
        tracker.Record(Terminal(1, ClipboardDiagnosticTerminalReason.ProcessingException));
        tracker.Record(Receive(2));
        tracker.Record(Terminal(2, ClipboardDiagnosticTerminalReason.Success));
        Assert.Empty(tray.ProtectionStatusHistory);           // still queued

        scheduler.RunAllPending();                            // several queued renders, run late and in order

        Assert.NotEmpty(tray.ProtectionStatusHistory);
        Assert.All(tray.ProtectionStatusHistory, h => Assert.Equal(ProtectionState.Verified, h.Presentation.State)); // never a stale Error
    }

    [Fact]
    public void AfterDispose_NoStatusReachesTheTray()
    {
        var tracker = new ClipboardProtectionStatusTracker(scanWatchdog: Timeout.InfiniteTimeSpan);
        var tray = new FakeTrayIconSurface();
        var (bridge, _) = NewBridge(tray, tracker);
        bridge.Start();
        bridge.Dispose();
        int before = tray.ProtectionStatusHistory.Count;

        tracker.Record(Receive(1));
        tracker.Record(Terminal(1, ClipboardDiagnosticTerminalReason.ProcessingException));

        Assert.Equal(before, tray.ProtectionStatusHistory.Count);
    }

    [Fact]
    public void TheBridgeSubscribesOnStart_AndLeavesNoSubscriptionAfterDisposeOrAFailedStart()
    {
        static int Subscribers(ClipboardProtectionStatusTracker t) =>
            (typeof(ClipboardProtectionStatusTracker).GetField("Changed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(t) as Delegate)?.GetInvocationList().Length ?? 0;

        var tracker = new ClipboardProtectionStatusTracker(scanWatchdog: Timeout.InfiniteTimeSpan);
        var (bridge, _) = NewBridge(new FakeTrayIconSurface(), tracker);
        Assert.Equal(0, Subscribers(tracker));
        bridge.Start();
        Assert.Equal(1, Subscribers(tracker));
        bridge.Dispose();
        Assert.Equal(0, Subscribers(tracker));

        var failing = new ClipboardProtectionStatusTracker(scanWatchdog: Timeout.InfiniteTimeSpan);
        var (failedStart, _) = NewBridge(new FakeTrayIconSurface { ThrowOnShow = new InvalidOperationException("synthetic") }, failing);
        Assert.Throws<InvalidOperationException>(failedStart.Start);
        Assert.Equal(0, Subscribers(failing));
    }

    [Fact]
    public void ADisplayFailure_NeverReachesProtection()
    {
        var tracker = new ClipboardProtectionStatusTracker(scanWatchdog: Timeout.InfiniteTimeSpan);
        var tray = new FakeTrayIconSurface();
        var (bridge, _) = NewBridge(tray, tracker);
        bridge.Start();
        tray.ThrowOnSetProtectionStatus = new InvalidOperationException("synthetic tray failure");

        tracker.Record(Receive(1));
        tracker.Record(Terminal(1, ClipboardDiagnosticTerminalReason.ProcessingException));

        Assert.Equal(ProtectionState.Error, tracker.Current.State); // the status itself is unaffected
    }

    [Fact]
    public void TheDisplayCarriesNoClipboardTextOrExceptionMessage()
    {
        var tracker = new ClipboardProtectionStatusTracker(scanWatchdog: Timeout.InfiniteTimeSpan);
        var tray = new FakeTrayIconSurface();
        var (bridge, _) = NewBridge(tray, tracker);
        bridge.Start();
        tracker.Record(Receive(1));
        tracker.Record(ClipboardDiagnosticEvent.AttemptTerminal(1, ClipboardDiagnosticTerminalReason.ProcessingException, "010-1234-5678 SSN 321-54-9876"));

        // Only the exception TYPE NAME slot exists; whatever a caller puts there is what would be shown, so the coordinator only
        // ever supplies ex.GetType().Name. The presentation itself never includes it.
        foreach (var (presentation, _) in tray.ProtectionStatusHistory)
        {
            Assert.DoesNotContain("010", presentation.Tooltip + presentation.Detail);
            Assert.DoesNotContain("321", presentation.Tooltip + presentation.Detail);
        }
    }

    // ================================================================================================================
    // Freshness: the display is never safer than what has been established
    // ================================================================================================================

    [Fact]
    public void ANewClipboardChange_ImmediatelyReplacesAnOldVerified_WithScanning()
    {
        var tracker = new ClipboardProtectionStatusTracker(scanWatchdog: Timeout.InfiniteTimeSpan);
        tracker.Record(Receive(1));
        tracker.Record(Terminal(1, ClipboardDiagnosticTerminalReason.Success));
        Assert.Equal(ProtectionState.Verified, tracker.Current.State);

        tracker.Record(Receive(2));

        var shown = ProtectionStatusPresenter.Present(tracker.Current);
        Assert.Equal(ProtectionState.Scanning, shown.State);
        Assert.DoesNotContain("rewrite to the protected form was confirmed", shown.Tooltip + shown.Detail);
    }

    [Fact]
    public void ANonTextOrUnsupportedChange_NeverMakesTheStatusSaferThanItWas()
    {
        var tracker = new ClipboardProtectionStatusTracker(scanWatchdog: Timeout.InfiniteTimeSpan);
        tracker.Record(Receive(1));
        tracker.Record(Terminal(1, ClipboardDiagnosticTerminalReason.Success));
        tracker.Record(Receive(2));
        tracker.Record(Terminal(2, ClipboardDiagnosticTerminalReason.NonTextNotification));
        Assert.Equal(ProtectionState.Ready, tracker.Current.State); // not Verified: the Verified one is no longer about the clipboard

        tracker.Record(Receive(3));
        tracker.Record(Terminal(3, ClipboardDiagnosticTerminalReason.ProcessingException));
        tracker.Record(Receive(4));
        tracker.Record(Terminal(4, ClipboardDiagnosticTerminalReason.UnauthorizedTarget));

        Assert.Equal(ProtectionState.Error, tracker.Current.State);
        Assert.True(ProtectionStatusPresenter.Present(tracker.Current).IsWarning);
    }

    [Fact]
    public void AnOlderAttemptsLateEvent_NeverClearsOrReplacesTheCurrentFailure_OnlyANewerCompletedScanDoes()
    {
        var tracker = new ClipboardProtectionStatusTracker(scanWatchdog: Timeout.InfiniteTimeSpan);
        tracker.Record(Receive(5));
        tracker.Record(Terminal(5, ClipboardDiagnosticTerminalReason.ProcessingException));

        tracker.Record(Terminal(4, ClipboardDiagnosticTerminalReason.Success));     // arrives late, from the past
        tracker.Record(Terminal(3, ClipboardDiagnosticTerminalReason.NoActionRequired));
        Assert.Equal(ProtectionState.Error, tracker.Current.State);
        Assert.True(tracker.Current.UnresolvedFailure);

        tracker.Record(Receive(6));
        tracker.Record(Terminal(6, ClipboardDiagnosticTerminalReason.Success));
        Assert.Equal(ProtectionState.Verified, tracker.Current.State);
        Assert.False(tracker.Current.UnresolvedFailure);
    }

    [Fact]
    public async Task AScanThatNeverReportsAnEnd_BecomesLimited_InsteadOfStayingScanningOrAnOldStatusForever()
    {
        var tracker = new ClipboardProtectionStatusTracker(scanWatchdog: TimeSpan.FromMilliseconds(150));
        _disposables.Add(tracker);
        int changes = 0;
        tracker.Changed += (_, _) => Interlocked.Increment(ref changes);
        tracker.Record(Receive(1));
        tracker.Record(Terminal(1, ClipboardDiagnosticTerminalReason.Success));
        tracker.Record(Receive(2)); // never ends

        await WaitAsync(() => tracker.Current.State == ProtectionState.Limited);

        Assert.True(tracker.Current.UnresolvedFailure);
        Assert.Equal("ScanDidNotComplete", tracker.Current.FailureTypeName);
        Assert.True(ProtectionStatusPresenter.Present(tracker.Current).IsWarning);
        Assert.True(changes >= 3);

        tracker.Record(Terminal(1, ClipboardDiagnosticTerminalReason.Success)); // an older attempt, late: ignored
        Assert.Equal(ProtectionState.Limited, tracker.Current.State);
    }

    [Fact]
    public async Task ATerminalEventDisarmsTheWatchdog_SoAHealthyScanIsNeverMarkedLimited()
    {
        var tracker = new ClipboardProtectionStatusTracker(scanWatchdog: TimeSpan.FromMilliseconds(120));
        _disposables.Add(tracker);
        tracker.Record(Receive(1));
        tracker.Record(Terminal(1, ClipboardDiagnosticTerminalReason.Success));

        await Task.Delay(500);

        Assert.Equal(ProtectionState.Verified, tracker.Current.State);
        Assert.False(tracker.Current.UnresolvedFailure);
    }

    [Fact]
    public void AMailboxThatRefusedTheWork_MakesTheNewContentLimitedAtOnce()
    {
        var tracker = new ClipboardProtectionStatusTracker(scanWatchdog: Timeout.InfiniteTimeSpan);
        tracker.Record(Receive(1));
        tracker.Record(ClipboardDiagnosticEvent.MailboxWriteAttempted(1, accepted: true));
        Assert.Equal(ProtectionState.Scanning, tracker.Current.State);

        tracker.Record(Receive(2));
        tracker.Record(ClipboardDiagnosticEvent.MailboxWriteAttempted(2, accepted: false));

        Assert.Equal(ProtectionState.Limited, tracker.Current.State);
        Assert.True(tracker.Current.UnresolvedFailure);
    }

    [Fact]
    public void ConcurrentEventsAndSubscribers_NeverFail_AndTheFinalStatusFollowsTheNewestAttempt()
    {
        var tracker = new ClipboardProtectionStatusTracker(scanWatchdog: Timeout.InfiniteTimeSpan);
        int seen = 0;
        tracker.Changed += (_, _) => { _ = ProtectionStatusPresenter.Present(tracker.Current); Interlocked.Increment(ref seen); };

        Parallel.For(1, 500, i =>
        {
            tracker.Record(Receive(i));
            tracker.Record(Terminal(i, i % 3 == 0 ? ClipboardDiagnosticTerminalReason.ProcessingException : ClipboardDiagnosticTerminalReason.Success));
        });
        tracker.Record(Receive(10_000));
        tracker.Record(Terminal(10_000, ClipboardDiagnosticTerminalReason.ProcessingException));

        Assert.Equal(ProtectionState.Error, tracker.Current.State);
        Assert.Equal(10_000, tracker.Current.AttemptId);
        Assert.True(seen > 0);
    }

    [Fact]
    public async Task ADisabledWatchdog_NeverFires()
    {
        var tracker = new ClipboardProtectionStatusTracker(scanWatchdog: Timeout.InfiniteTimeSpan);
        tracker.Record(Receive(1));

        await Task.Delay(300);

        Assert.Equal(ProtectionState.Scanning, tracker.Current.State);
    }

    // ================================================================================================================
    // End to end: the real coordinator and processor -> tracker -> bridge -> tray
    // ================================================================================================================

    private sealed class TestOnlySource(CountrySettingObservation initial) : ICountrySettingSource
    {
        public Exception? FailWith { get; set; }
        public CountrySettingObservation Read() => FailWith is { } ex ? throw ex : initial;
    }

    private static IDetector[] Legacy() =>
    [
        new PhoneDetector(), new EmailDetector(), new ResidentRegistrationNumberDetector(), new CardNumberDetector(),
        new BankAccountNumberDetector(), new SecretDetector(), new IpAddressDetector(), new MacAddressDetector(),
        new GpsCoordinateDetector(),
    ];

    [Fact]
    public async Task EndToEnd_AFailedScanIsShownToTheUser_ARecoveryClearsIt_AndTheClipboardIsNeverWrittenOnFailure()
    {
        var tracker = new ClipboardProtectionStatusTracker(scanWatchdog: Timeout.InfiniteTimeSpan);
        var tray = new FakeTrayIconSurface();
        var (bridge, _) = NewBridge(tray, tracker);
        bridge.Start();

        var source = new TestOnlySource(CountrySettingObservation.AuthenticatedOn);
        var host = new CountryScopedPipelineHost(source, s => UsOptInPipeline.Create(Legacy(), unitedStatesSetting: s));
        var read = new FakeClipboardReadTransport();
        var write = new FakeClipboardWriteTransport { NextResult = ClipboardWriteResult.Success(resultSequence: 2) };
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        using var coordinator = new ClipboardPrivacyCoordinator(
            read, new FakeForegroundTargetCapture(), new ClipboardPrivacyProcessor(() => host.Current(), new FakeTrustExceptionProvider()),
            write, lifecycle, new ClipboardDecisionSessionPublisher(lifecycle), new ClipboardOperationGate(),
            new FakeClipboardComposerVerificationHandoff(), new FakeClipboardComposerVerificationInvalidation(), diagnostics: tracker);
        coordinator.Start();

        async Task ScanAsync(uint seq, string text)
        {
            long before = tracker.Current.CompletedAttempts;
            read.NextReadResult = ClipboardTextReadResult.Success(new ClipboardTextSnapshot(seq, true, text));
            read.RaiseChanged(new ClipboardChangeNotification(seq, true, true));
            await WaitAsync(() => tracker.Current.CompletedAttempts > before);
        }

        await ScanAsync(1, "연락처 010-1234-5678");
        Assert.Equal(ProtectionState.Verified, tray.ProtectionStatusHistory[^1].Presentation.State);
        Assert.Equal(1, write.CallCount);

        source.FailWith = new IOException("synthetic read failure");
        await ScanAsync(2, "연락처 010-9999-8888");
        await ScanAsync(3, "연락처 010-7777-6666");
        var alerts = tray.ProtectionStatusHistory.Where(h => h.Notify).ToList();

        Assert.Equal(ProtectionState.Error, tray.ProtectionStatusHistory[^1].Presentation.State);
        Assert.Single(alerts);
        Assert.Equal(RequiredWarning, alerts[0].Presentation.Detail);
        Assert.Equal(1, write.CallCount);       // no write on failure: the original text is neither deleted nor overwritten

        source.FailWith = null;
        await ScanAsync(4, "연락처 010-5555-4444");

        Assert.Equal(ProtectionState.Verified, tray.ProtectionStatusHistory[^1].Presentation.State);
        Assert.False(tray.ProtectionStatusHistory[^1].Presentation.IsWarning);
        Assert.Equal(2, write.CallCount);
    }

    // ================================================================================================================
    // The shipped product and the real tray (structure only)
    // ================================================================================================================

    private static string AppSource(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "Privon.App", file))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "Privon.App", file));
    }

    [Fact]
    public void TheShippedBridgeAndTray_KeepTheRunningTooltip_AndWireTheStatusFromTheComposition()
    {
        string bridge = AppSource("PrivonAppUiBridge.cs");
        string tray = AppSource("WinFormsTrayIconSurface.cs");

        Assert.Contains("composition.ProtectionStatus", bridge);
        Assert.Contains("Running", tray);                     // the shipped tooltip is unchanged
        Assert.DoesNotContain("Protecting", tray);
        Assert.Contains("IProtectionStatusSurface", tray);
        Assert.Contains("ToolTipIcon.Warning", tray);
        Assert.Contains("_lastBalloonIsChromeReconnect", tray); // a warning balloon click is never read as a Chrome reconnect click
    }

    [Fact]
    public void TheCompositionBuildsTheTrackerUnconditionally_SoTheShippedBridgeShowsTheStatus()
    {
        // This used to assert that a never-started shipped composition had no tracker (vacuous: the tracker is built at Start). The behavioral proof
        // that a STARTED shipped composition has one is ShippedProtectionWarningTests.TheShippedComposition_BuildsTheProtectionStatusTracker.
        string normalized = AppSource("PrivonAppComposition.cs").Replace("\r\n", "\n");

        Assert.Contains("_protectionStatus = new ClipboardProtectionStatusTracker(_diagnostics);", normalized);
        Assert.DoesNotContain("if (_detectionPipelineSource is not null)\n        {\n            _protectionStatus", normalized);
    }
}
