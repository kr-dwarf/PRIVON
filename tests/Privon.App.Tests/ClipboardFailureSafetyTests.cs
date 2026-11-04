using System.Reflection;
using System.Text.RegularExpressions;
using Privon.App;
using Privon.Core;
using Privon.Detection;
using Privon.Detection.Detectors;
using Privon.Detection.Scope;
using Privon.UsProductIntegration;
using Privon.Windows;

namespace Privon.App.Tests;

// SYNTHETIC DATA ONLY. A-1: what the product records when a scan FAILS (a detector throws, the family arbiter is missing, a
// required detector is not ready, the country setting cannot be read, or the pipeline cannot be rebuilt). Every test drives
// the REAL ClipboardPrivacyCoordinator, ClipboardPrivacyProcessor, ClipboardDecisionActionResolver and ClipboardDecisionScope-
// Lifecycle with the same fake clipboard transports the other suites use; nothing touches the real clipboard, a hook or a window.
//
// CAPABILITY FACT (checked below against the production P/Invoke inventory): the product has NO send-interception or blocking
// capability. It cannot stop a paste or a send. What it can do, and what these tests pin, is never to CLAIM protection it did
// not verify, to keep a failure visible until a later scan completes, and to leave the clipboard text untouched on failure.
public sealed class ClipboardFailureSafetyTests : IDisposable
{
    private readonly List<Rig> _rigs = [];

    public void Dispose()
    {
        foreach (var rig in _rigs) rig.Dispose();
    }

    private sealed class TestOnlySource(CountrySettingObservation initial) : ICountrySettingSource
    {
        private int _value = (int)initial;
        public Exception? FailWith { get; set; }
        public void Set(CountrySettingObservation value) => Interlocked.Exchange(ref _value, (int)value);

        public CountrySettingObservation Read() =>
            FailWith is { } ex ? throw ex : (CountrySettingObservation)Volatile.Read(ref _value);
    }

    private sealed class ThrowingSecretDetector : IDetector
    {
        public string Name => "ThrowingSecretDetector";
        public PiiType PiiType => PiiType.Secret;
        public DetectorId Id { get; } = DetectorId.Create("global.secret");
        public int ResolutionRank => 10;
        public DetectorReadinessState Readiness => DetectorReadinessState.Ready;
        public IReadOnlyList<DetectionCandidate> Detect(DetectionContext context) =>
            throw new InvalidOperationException("synthetic detector failure");
    }

    private sealed class SwitchableEmailDetector : IDetector
    {
        private readonly EmailDetector _inner = new();
        public DetectorReadinessState State { get; set; } = DetectorReadinessState.Ready;
        public string Name => "SwitchableEmailDetector";
        public PiiType PiiType => PiiType.Email;
        public DetectorId Id => _inner.Id;
        public int ResolutionRank => _inner.ResolutionRank;
        public DetectorReadinessState Readiness => State;
        public IReadOnlyList<DetectionCandidate> Detect(DetectionContext context) => _inner.Detect(context);
    }

    private static IDetector[] Legacy() =>
    [
        new PhoneDetector(), new EmailDetector(), new ResidentRegistrationNumberDetector(), new CardNumberDetector(),
        new BankAccountNumberDetector(), new SecretDetector(), new IpAddressDetector(), new MacAddressDetector(),
        new GpsCoordinateDetector(),
    ];

    private static DetectionPipeline Build(CountrySettingObservation setting) =>
        UsOptInPipeline.Create(Legacy(), unitedStatesSetting: setting);

    private sealed class Rig : IDisposable
    {
        private uint _sequence;

        public Rig(Func<DetectionPipeline> source)
        {
            Lifecycle = new ClipboardDecisionScopeLifecycle();
            Tracker = new ClipboardProtectionStatusTracker();
            Write = new FakeClipboardWriteTransport { NextResult = ClipboardWriteResult.Success(resultSequence: 2) };
            Processor = new ClipboardPrivacyProcessor(source, new FakeTrustExceptionProvider());
            Coordinator = new ClipboardPrivacyCoordinator(
                Read, Target, Processor, Write, Lifecycle, new ClipboardDecisionSessionPublisher(Lifecycle), new ClipboardOperationGate(),
                new FakeClipboardComposerVerificationHandoff(), new FakeClipboardComposerVerificationInvalidation(),
                diagnostics: Tracker, foregroundTrigger: Foreground);
            Coordinator.Start();
        }

        public FakeClipboardReadTransport Read { get; } = new();
        public FakeForegroundTargetCapture Target { get; } = new();
        public FakeClipboardForegroundTrigger Foreground { get; } = new();
        public FakeClipboardWriteTransport Write { get; }
        public ClipboardDecisionScopeLifecycle Lifecycle { get; }
        public ClipboardProtectionStatusTracker Tracker { get; }
        public ClipboardPrivacyProcessor Processor { get; }
        public ClipboardPrivacyCoordinator Coordinator { get; }

        public async Task<ClipboardProtectionStatus> ScanAsync(string text)
        {
            long before = Tracker.Current.CompletedAttempts;
            _sequence++;
            Read.NextReadResult = ClipboardTextReadResult.Success(new ClipboardTextSnapshot(_sequence, true, text));
            Read.RaiseChanged(new ClipboardChangeNotification(_sequence, true, true));
            await WaitAsync(() => Tracker.Current.CompletedAttempts > before);
            return Tracker.Current;
        }

        public async Task<ClipboardProtectionStatus> ForegroundRetryAsync()
        {
            long before = Tracker.Current.CompletedAttempts;
            Foreground.Raise();
            await WaitAsync(() => Tracker.Current.CompletedAttempts > before);
            return Tracker.Current;
        }

        public void Dispose() => Coordinator.Dispose();
    }

    private Rig NewRig(Func<DetectionPipeline> source)
    {
        var rig = new Rig(source);
        _rigs.Add(rig);
        return rig;
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

    private const string Phone = "연락처 010-1234-5678";
    private const string UsSsn = "SSN: 321-54-9876";

    // ================================================================================================================
    // Normal scans: a state is claimed only for what was verified
    // ================================================================================================================

    [Fact]
    public async Task AVerifiedRewrite_IsTheOnlyThingThatClaimsProtection()
    {
        var rig = NewRig(() => Build(CountrySettingObservation.AuthenticatedOn));

        var status = await rig.ScanAsync(Phone);

        Assert.Equal(ProtectionState.Verified, status.State);
        Assert.True(status.ClaimsProtected);
        Assert.False(status.UnresolvedFailure);
        Assert.Equal(1, rig.Write.CallCount);
    }

    [Fact]
    public async Task APendingLevel3Decision_NeedsDecision_NotProtected_AndTheRawTextIsRecordedAsStillThere()
    {
        var rig = NewRig(() => Build(CountrySettingObservation.AuthenticatedOn));

        var status = await rig.ScanAsync(UsSsn);

        Assert.Equal(ProtectionState.NeedsDecision, status.State);
        Assert.False(status.ClaimsProtected);
        Assert.True(status.UnprotectedTextMayRemain);
        Assert.Equal(0, rig.Write.CallCount);
        Assert.True(rig.Lifecycle.HasActiveScope);
    }

    [Fact]
    public async Task ACompletedScanThatFindsNothing_IsReady_WhichIsNotProtected()
    {
        var rig = NewRig(() => Build(CountrySettingObservation.AuthenticatedOn));

        var status = await rig.ScanAsync("오늘 날씨가 좋네요");

        Assert.Equal(ProtectionState.Ready, status.State);
        Assert.False(status.ClaimsProtected);
        Assert.False(status.UnresolvedFailure);
    }

    // ================================================================================================================
    // Failures: each is an Error, never silence, never Verified, and the clipboard text is left exactly as it was
    // ================================================================================================================

    public static TheoryData<string> FailureKinds => new()
    {
        "detector-throws", "arbiter-missing", "required-detector-not-ready", "country-setting-unreadable", "rebuild-fails",
    };

    private Rig FailingRig(string kind)
    {
        switch (kind)
        {
            case "detector-throws":
                return NewRig(() => UsOptInPipeline.Create([.. Legacy().Where(d => d.Id.Value != "global.secret"), new ThrowingSecretDetector()],
                    unitedStatesSetting: CountrySettingObservation.AuthenticatedOn));
            case "arbiter-missing":
                return NewRig(() => UsOptInPipeline.Create(Legacy(), new Privon.Detection.Family.EmptyDetectorFamilyArbiterProvider(),
                    unitedStatesSetting: CountrySettingObservation.AuthenticatedOn));
            case "required-detector-not-ready":
                var notReady = new SwitchableEmailDetector { State = DetectorReadinessState.UnavailableAuthoritySnapshot };
                return NewRig(() => UsOptInPipeline.Create(
                    [.. Legacy().Where(d => d.Id.Value != "global.email"), notReady], unitedStatesSetting: CountrySettingObservation.AuthenticatedOn));
            case "country-setting-unreadable":
                var unreadable = new TestOnlySource(CountrySettingObservation.AuthenticatedOn) { FailWith = new IOException("synthetic read failure") };
                var host = new CountryScopedPipelineHost(unreadable, Build);
                return NewRig(() => host.Current());
            case "rebuild-fails":
                var source = new TestOnlySource(CountrySettingObservation.AuthenticatedOn);
                var rebuild = new CountryScopedPipelineHost(source, _ => throw new InvalidOperationException("synthetic rebuild failure"));
                return NewRig(() => rebuild.Current());
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    [Theory]
    [MemberData(nameof(FailureKinds))]
    public async Task AFailedScan_IsRecordedAsAnUnresolvedError_NeverAsVerifiedOrAsNothingToProtect(string kind)
    {
        var rig = FailingRig(kind);

        var status = await rig.ScanAsync(Phone);

        Assert.Equal(ProtectionState.Error, status.State);
        Assert.True(status.UnresolvedFailure);
        Assert.False(status.ClaimsProtected);
        Assert.True(status.UnprotectedTextMayRemain);
        Assert.NotNull(status.FailureTypeName);
    }

    [Theory]
    [MemberData(nameof(FailureKinds))]
    public async Task AFailedScan_NeverWritesTheClipboard_TheOriginalTextIsNeitherDeletedNorOverwritten(string kind)
    {
        var rig = FailingRig(kind);

        await rig.ScanAsync(Phone);

        Assert.Equal(0, rig.Write.CallCount);
        Assert.Empty(rig.Write.ReceivedReplacementTexts);
    }

    [Fact]
    public async Task TheDifferentFailuresStayDistinguishable()
    {
        var names = new Dictionary<string, string?>();
        foreach (string kind in new[] { "detector-throws", "arbiter-missing", "required-detector-not-ready", "country-setting-unreadable" })
        {
            names[kind] = (await FailingRig(kind).ScanAsync(Phone)).FailureTypeName;
        }

        Assert.Equal(nameof(InvalidOperationException), names["detector-throws"]);
        Assert.Equal("FamilyArbitrationUnavailableException", names["arbiter-missing"]);
        Assert.Equal("RequiredDetectorNotRegisteredException", names["required-detector-not-ready"]);
        Assert.Equal(nameof(IOException), names["country-setting-unreadable"]);
    }

    [Fact]
    public async Task TheStatusNeverCarriesTheTextOrAnyPartOfIt()
    {
        var rig = FailingRig("detector-throws");

        var status = await rig.ScanAsync("연락처 010-1234-5678 SSN: 321-54-9876");

        string shown = status.ToString() + status.FailureTypeName;
        Assert.DoesNotContain("010", shown);
        Assert.DoesNotContain("1234", shown);
        Assert.DoesNotContain("321", shown);
    }

    // ================================================================================================================
    // Sequences: no reuse of an earlier plan, no silent release of a failure, recovery only by a completed scan
    // ================================================================================================================

    [Fact]
    public async Task AFailureAfterAVerifiedScan_ReplacesTheVerifiedStatus_AndTheEarlierWritePlanIsNeverReused()
    {
        var source = new TestOnlySource(CountrySettingObservation.AuthenticatedOff);
        bool failOn = false;
        var host = new CountryScopedPipelineHost(source, s =>
            s == CountrySettingObservation.AuthenticatedOn && failOn ? throw new InvalidOperationException("synthetic rebuild failure") : Build(s));
        var rig = NewRig(() => host.Current());

        var first = await rig.ScanAsync(Phone);
        Assert.Equal(ProtectionState.Verified, first.State);
        Assert.Equal(1, rig.Write.CallCount);

        failOn = true;
        source.Set(CountrySettingObservation.AuthenticatedOn); // OFF -> ON with a failing rebuild
        var second = await rig.ScanAsync("다른 연락처 010-9999-8888");

        Assert.Equal(ProtectionState.Error, second.State);
        Assert.True(second.UnresolvedFailure);
        Assert.Equal(1, rig.Write.CallCount); // the first plan was not replayed onto the new text
        Assert.Single(rig.Write.ReceivedReplacementTexts);
    }

    [Fact]
    public async Task AFailureAfterAPendingDecision_LeavesNoUsableDecisionSession_AndTheOldItemCannotBeApplied()
    {
        var source = new TestOnlySource(CountrySettingObservation.AuthenticatedOn);
        bool failNext = false;
        var host = new CountryScopedPipelineHost(source, s => failNext ? throw new InvalidOperationException("synthetic") : Build(s));
        var rig = NewRig(() =>
        {
            if (failNext) throw new InvalidOperationException("synthetic pipeline failure");
            return host.Current();
        });
        var pending = await rig.ScanAsync(UsSsn);
        var oldScope = rig.Lifecycle.GetActiveScope()!;
        var oldItem = Assert.Single(oldScope.Items);
        Assert.Equal(ProtectionState.NeedsDecision, pending.State);

        failNext = true;
        var failed = await rig.ScanAsync("SSN: 123-45-6789");

        Assert.Equal(ProtectionState.Error, failed.State);
        Assert.False(rig.Lifecycle.HasActiveScope); // the new clipboard content invalidated the old session
        rig.Read.NextReadResult = ClipboardTextReadResult.Success(new ClipboardTextSnapshot(9, true, UsSsn));
        var resolver = new ClipboardDecisionActionResolver(
            new ClipboardOperationGate(), rig.Lifecycle, rig.Target, rig.Read, rig.Write, rig.Processor,
            new FakeClipboardComposerVerificationHandoff());
        var result = await resolver.ResolveAsync(oldScope, oldItem, ClipboardDecisionIntent.Protect);

        Assert.NotEqual(ClipboardDecisionActionOutcome.Applied, result.Outcome);
        Assert.Equal(0, rig.Write.CallCount);
    }

    [Fact]
    public async Task ARejectedWriteOrRead_IsLimited_NeverVerified()
    {
        var rig = NewRig(() => Build(CountrySettingObservation.AuthenticatedOn));
        rig.Write.NextResult = ClipboardWriteResult.Failure(ClipboardWriteOutcome.SequenceChanged, mutated: false);

        var rejectedWrite = await rig.ScanAsync(Phone);

        Assert.Equal(ProtectionState.Limited, rejectedWrite.State);
        Assert.True(rejectedWrite.UnresolvedFailure);
        Assert.False(rejectedWrite.ClaimsProtected);

        long before = rig.Tracker.Current.CompletedAttempts;
        rig.Read.NextReadResult = ClipboardTextReadResult.Failure(ClipboardReadOutcome.NativeFailure);
        rig.Read.RaiseChanged(new ClipboardChangeNotification(77, true, true));
        await WaitAsync(() => rig.Tracker.Current.CompletedAttempts > before);

        Assert.Equal(ProtectionState.Limited, rig.Tracker.Current.State);
        Assert.False(rig.Tracker.Current.ClaimsProtected);
    }

    [Fact]
    public async Task AFailureIsNotReleasedByAnAttemptThatNeverScanned()
    {
        var rig = FailingRig("detector-throws");
        Assert.Equal(ProtectionState.Error, (await rig.ScanAsync(Phone)).State);

        rig.Target.SnapshotToReturn = new ForegroundTargetSnapshot(IsResolved: true, ProcessId: 9999, ProcessName: "notepad");
        var unauthorized = await rig.ScanAsync(Phone); // an unsupported target: the text was never scanned

        Assert.Equal(ProtectionState.Error, unauthorized.State);
        Assert.True(unauthorized.UnresolvedFailure);
    }

    [Fact]
    public async Task OnlyACompletedScanReleasesAFailure_AndTheForegroundRetryRecoversTheSameClipboardText()
    {
        var source = new TestOnlySource(CountrySettingObservation.AuthenticatedOn);
        bool failOn = true;
        var host = new CountryScopedPipelineHost(source, s => failOn ? throw new InvalidOperationException("synthetic") : Build(s));
        var rig = NewRig(() => host.Current());
        Assert.Equal(ProtectionState.Error, (await rig.ScanAsync(Phone)).State);

        failOn = false;
        var retried = await rig.ForegroundRetryAsync(); // the abandoned evaluation is attempted again on the next trigger

        Assert.Equal(ProtectionState.Verified, retried.State);
        Assert.False(retried.UnresolvedFailure);
        Assert.Equal(1, rig.Write.CallCount);
    }

    [Fact]
    public async Task ACleanScanOfNewContent_AlsoReleasesAFailure_ToReadyOrNeedsDecision()
    {
        var source = new TestOnlySource(CountrySettingObservation.AuthenticatedOn);
        bool failOn = true;
        var host = new CountryScopedPipelineHost(source, s => failOn ? throw new InvalidOperationException("synthetic") : Build(s));
        var rig = NewRig(() => host.Current());
        await rig.ScanAsync(Phone);

        failOn = false;
        var recovered = await rig.ScanAsync("오늘 날씨가 좋네요");

        Assert.Equal(ProtectionState.Ready, recovered.State);
        Assert.False(recovered.UnresolvedFailure);
        Assert.False(recovered.ClaimsProtected);
    }

    [Fact]
    public async Task WhileAFailureIsUnresolved_ANewScanInProgressStillReportsIt()
    {
        var gate = new SemaphoreSlim(0, 1);
        var source = new TestOnlySource(CountrySettingObservation.AuthenticatedOn);
        bool failOn = true;
        var host = new CountryScopedPipelineHost(source, s => failOn ? throw new InvalidOperationException("synthetic") : Build(s));
        var rig = NewRig(() => host.Current());
        await rig.ScanAsync(Phone);
        failOn = false;

        // Observe the in-progress state through the tracker directly, between the start event and the terminal one.
        rig.Tracker.Record(ClipboardDiagnosticEvent.AppClipboardChangeReceived(999, 1, true, true));

        Assert.Equal(ProtectionState.Scanning, rig.Tracker.Current.State);
        Assert.True(rig.Tracker.Current.UnresolvedFailure);
        Assert.False(rig.Tracker.Current.ClaimsProtected);
        gate.Dispose();
    }

    // ================================================================================================================
    // Legitimate paths keep their meaning
    // ================================================================================================================

    [Fact]
    public async Task ALegitimateLevel3BypassOnce_StillNeedsTheSecondConfirmation_AndNeverBecomesVerified()
    {
        var rig = NewRig(() => Build(CountrySettingObservation.AuthenticatedOn));
        await rig.ScanAsync(UsSsn);
        var scope = rig.Lifecycle.GetActiveScope()!;
        var item = Assert.Single(scope.Items);
        var resolver = new ClipboardDecisionActionResolver(
            new ClipboardOperationGate(), rig.Lifecycle, rig.Target, rig.Read, rig.Write, rig.Processor,
            new FakeClipboardComposerVerificationHandoff());

        rig.Read.NextReadResult = ClipboardTextReadResult.Success(new ClipboardTextSnapshot(1, true, UsSsn));
        var first = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);
        rig.Read.NextReadResult = ClipboardTextReadResult.Success(new ClipboardTextSnapshot(1, true, UsSsn));
        var second = await resolver.ResolveAsync(scope, item, ClipboardDecisionIntent.BypassOnce);

        Assert.Equal(ClipboardDecisionActionOutcome.AwaitingSecondConfirmation, first.Outcome);
        Assert.Equal(ClipboardDecisionActionOutcome.Applied, second.Outcome);
        Assert.Equal(0, rig.Write.CallCount);
        Assert.Equal(ProtectionState.NeedsDecision, rig.Tracker.Current.State); // a user bypass is not a protection claim
        Assert.False(rig.Tracker.Current.ClaimsProtected);
    }

    [Fact]
    public async Task KoreanAndGlobalTextBehaveAsBefore_UnderTheStatusTracker()
    {
        var rig = NewRig(() => Build(CountrySettingObservation.AuthenticatedOff));

        var phone = await rig.ScanAsync(Phone);
        var rrn = await rig.ScanAsync("901231-1234567");

        Assert.Equal(ProtectionState.Verified, phone.State);
        Assert.Equal(ProtectionState.NeedsDecision, rrn.State);
        Assert.Equal(1, rig.Write.CallCount);
    }

    // ================================================================================================================
    // Capability facts and the shipped composition
    // ================================================================================================================

    [Fact]
    public void TheProduct_HasNoSendInterceptionOrBlockingCapability_SoNoStateEverClaimsABlockedTransmission()
    {
        var banned = new[] { "RegisterHotKey", "UnregisterHotKey", "SetWindowsHookEx", "BlockInput", "SendInput", "keybd_event", "mouse_event" };
        var declared = new List<string>();
        foreach (string project in new[] { "Privon.App", "Privon.Windows", "Privon.Browser", "Privon.Core", "Privon.Detection", "Privon.Storage" })
        {
            foreach (string file in Directory.EnumerateFiles(FindSourceDirectory(project), "*.cs", SearchOption.AllDirectories))
            {
                foreach (string line in File.ReadLines(file))
                {
                    string code = line.TrimStart();
                    if (code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith("///", StringComparison.Ordinal)) continue;
                    if (Regex.IsMatch(code, @"\b(static extern|static partial)\b")) declared.Add(code);
                }
            }
        }

        Assert.NotEmpty(declared); // the inventory is real: the clipboard and window-event P/Invokes are found
        Assert.DoesNotContain(declared, d => banned.Any(b => d.Contains(b, StringComparison.Ordinal)));
        Assert.False(ClipboardProtectionStatusTracker.TransmitBlockingAvailable);
    }

    [Fact]
    public void BothTheShippedAndTheOptInCompositions_RecordFromTheirRealCoordinator()
    {
        string root = Path.Combine(Path.GetTempPath(), "ClipboardFailureSafety", Guid.NewGuid().ToString("N"));
        using var shipped = new PrivonAppComposition(root + "-a", new SessionLockNotificationAdapter(), new ClipboardChangeMonitor(),
            new ComposerTextReader(), new ForegroundChangeMonitor());
        using var optIn = new PrivonAppComposition(root + "-b", new SessionLockNotificationAdapter(), new ClipboardChangeMonitor(),
            new ComposerTextReader(), new ForegroundChangeMonitor(), enableDiagnostics: false,
            detectionPipelineSource: () => Build(CountrySettingObservation.AuthenticatedOn));
        shipped.Start();
        optIn.Start();

        object? Diagnostics(PrivonAppComposition c)
        {
            var coordinator = typeof(PrivonAppComposition).GetField("_coordinator", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(c)!;
            return coordinator.GetType().GetField("_diagnostics", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(coordinator);
        }

        // Changed by the Controller-approved shipped status warning: the shipped composition now records from its coordinator too.
        Assert.NotNull(shipped.ProtectionStatus);
        Assert.Same(shipped.ProtectionStatus, Diagnostics(shipped));
        Assert.Equal(ProtectionState.Ready, shipped.ProtectionStatus!.Current.State);
        Assert.NotNull(optIn.ProtectionStatus);
        Assert.Same(optIn.ProtectionStatus, Diagnostics(optIn));
        Assert.Equal(ProtectionState.Ready, optIn.ProtectionStatus!.Current.State);

        foreach (var dir in new[] { root + "-a", root + "-b" })
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // ================================================================================================================
    // The aggregator itself
    // ================================================================================================================

    [Fact]
    public void TheTracker_IgnoresALateEventFromAnOlderAttempt_AndIsSafeUnderConcurrentEvents()
    {
        var tracker = new ClipboardProtectionStatusTracker();
        tracker.Record(ClipboardDiagnosticEvent.AppClipboardChangeReceived(5, 1, true, true));
        tracker.Record(ClipboardDiagnosticEvent.AttemptTerminal(5, ClipboardDiagnosticTerminalReason.ProcessingException, "InvalidOperationException"));
        tracker.Record(ClipboardDiagnosticEvent.AttemptTerminal(3, ClipboardDiagnosticTerminalReason.Success)); // late, older

        Assert.Equal(ProtectionState.Error, tracker.Current.State);

        var racing = new ClipboardProtectionStatusTracker();
        Parallel.For(1, 400, i =>
        {
            racing.Record(ClipboardDiagnosticEvent.AppClipboardChangeReceived(i, 1, true, true));
            racing.Record(ClipboardDiagnosticEvent.AttemptTerminal(
                i, i % 2 == 0 ? ClipboardDiagnosticTerminalReason.Success : ClipboardDiagnosticTerminalReason.ProcessingException, "X"));
        });
        racing.Record(ClipboardDiagnosticEvent.AppClipboardChangeReceived(1000, 1, true, true));
        racing.Record(ClipboardDiagnosticEvent.AttemptTerminal(1000, ClipboardDiagnosticTerminalReason.ProcessingException, "Y"));

        Assert.Equal(ProtectionState.Error, racing.Current.State);
        Assert.Equal(1000, racing.Current.AttemptId);
    }

    [Fact]
    public void TheTracker_ForwardsToItsInnerRecorder_AndAnInnerFailureDoesNotChangeTheStatus()
    {
        var inner = new FakeClipboardDiagnosticRecorder();
        var tracker = new ClipboardProtectionStatusTracker(inner);
        tracker.Record(ClipboardDiagnosticEvent.AttemptTerminal(1, ClipboardDiagnosticTerminalReason.Success));
        Assert.Single(inner.Events);

        var throwing = new ClipboardProtectionStatusTracker(new ThrowingRecorder());
        throwing.Record(ClipboardDiagnosticEvent.AttemptTerminal(1, ClipboardDiagnosticTerminalReason.ProcessingException, "X"));
        Assert.Equal(ProtectionState.Error, throwing.Current.State);
    }

    private sealed class ThrowingRecorder : IClipboardDiagnosticRecorder
    {
        public void Record(ClipboardDiagnosticEvent diagnosticEvent) => throw new InvalidOperationException("synthetic recorder failure");
    }

    private static string FindSourceDirectory(string project)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", project))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "src", project);
    }
}
