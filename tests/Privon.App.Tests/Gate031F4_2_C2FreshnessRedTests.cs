using System.Reflection;
using Privon.App;
using Privon.Browser;
using Privon.Core;
using Privon.Windows;

namespace Privon.App.Tests;

// PRIVON 0.3.1 Gate 031F4.2 -- Phase-C2 App freshness BEHAVIORAL RED suite, against the contract
// frozen by Gate 031F4.1. Every test below is a COMPLETE behavioral test: it builds real state from
// the real production WebChannelRegistry / WebChallengeProof / ForegroundTargetSnapshot types, drives
// the real FakeClipboardForegroundTrigger, and asserts the exact required outcome. The only thing
// standing between each test and GREEN is the two production types that do not exist yet:
//
//   Privon.App.IForegroundEpochSource            { long CurrentEpoch { get; } }
//   Privon.App.ForegroundEpochTracker            : IForegroundEpochSource, IDisposable
//   Privon.App.WebClipboardAuthorizationFreshness : Privon.Windows.IClipboardAuthorizationFreshness
//
// Those are located by reflection at the TOP of each test (RED_LOCATE), with a failure message
// carrying that specific test's own required behavior. Everything after the locate step is real
// behavioral code that begins executing, unchanged, the moment the production types land -- this is
// deliberately NOT the weaker "prove the class is absent" shape (Gate 031F4.2 RED_INTEGRITY).
//
// Reflection is confined to: locating the missing production seam, reading CurrentEpoch /
// EstablishInitialEpoch on the tracker, structural shape checks (R16), and the exhaustion
// private-state technique (R14) -- exactly the four uses Gate 031F4.2 permits. Once the freshness
// object is constructed it is cast to the REAL, public Privon.Windows.IClipboardAuthorizationFreshness
// and invoked normally, so every IsStillCurrent() assertion below is a genuine interface call.
//
// NO production type is created, faked, or stubbed anywhere in this file.
public class Gate031F4_2_C2FreshnessRedTests
{
    private static Assembly AppAssembly => typeof(TargetGate).Assembly;

    private const string EpochSourceInterfaceName = "Privon.App.IForegroundEpochSource";
    private const string TrackerTypeName = "Privon.App.ForegroundEpochTracker";
    private const string FreshnessTypeName = "Privon.App.WebClipboardAuthorizationFreshness";

    private static Type? EpochSourceInterface => AppAssembly.GetType(EpochSourceInterfaceName);
    private static Type? TrackerType => AppAssembly.GetType(TrackerTypeName);
    private static Type? FreshnessType => AppAssembly.GetType(FreshnessTypeName);

    // A deliberately NON-product origin. The freshness algorithm never reads Origin at all (Gate
    // 031F4.1 PRIVACY), and WebChannelRegistry.TryAssert only requires a non-empty string when
    // OriginResolution.Resolved -- so no supported-product origin belongs anywhere in this file.
    private const string NeutralOrigin = "https://example.invalid";

    private const uint BrowserPid = 7777;

    private const string TrackerContract =
        "Privon.App.ForegroundEpochTracker does not exist yet (Gate 031F4.1 EPOCH_TRACKER, frozen). " +
        "Required: internal sealed class ForegroundEpochTracker : IForegroundEpochSource, IDisposable, " +
        "constructed with an IClipboardForegroundTrigger which it subscribes to in its own constructor. " +
        "long CurrentEpoch { get; } -- synchronous, thread-safe, allocation-free, no timestamps, no " +
        "grace period, no persistence, no logging. Initial value 0 (UNESTABLISHED_ZERO_UNTIL_CAPTURE). " +
        "Valid epoch domain is 1..long.MaxValue: strictly monotonic, never reused, never negative, " +
        "never wrapping. 0 is not an epoch -- it is the single fail-closed sentinel for unestablished, " +
        "exhausted, and disposed. Exactly one increment per accepted Changed event (no dedup, no " +
        "debounce, no coalescing, no HWND comparison -- the trigger is structurally payload-free). " +
        "EstablishInitialEpoch() performs the 0 -> 1 transition only, is a no-op when CurrentEpoch > 0, " +
        "and must never revive an exhausted or disposed tracker.";

    private const string FreshnessContract =
        "Privon.App.WebClipboardAuthorizationFreshness does not exist yet (Gate 031F4.1 " +
        "FRESHNESS_IMPLEMENTATION, frozen). Required: internal sealed class implementing " +
        "Privon.Windows.IClipboardAuthorizationFreshness, constructed with exactly four dependencies " +
        "in this order -- WebChallengeProof proof, WebChannelRegistry registry, IForegroundEpochSource " +
        "epochSource, IForegroundTargetCapture foregroundCapture -- retaining only the bound proof and " +
        "those references (never Origin, never WebForegroundEvidence, never SupportedWebTarget, never " +
        "a browser/product identity, never a timestamp, never the authorized snapshot). " +
        "IsStillCurrent() runs the frozen 15-step order with NO memoization, re-reading all live state " +
        "on every call, and returns false immediately at the first failing step.";

    // ==================================================================
    // REFLECTION HARNESS -- locate-only; every assertion below it is real behavior.
    // ==================================================================

    private static object RequireTracker(IClipboardForegroundTrigger trigger, string requiredBehavior)
    {
        Assert.True(TrackerType is not null, TrackerContract + " REQUIRED FOR THIS TEST: " + requiredBehavior);
        object? instance = Activator.CreateInstance(TrackerType!, trigger);
        Assert.NotNull(instance);
        return instance!;
    }

    private static long CurrentEpoch(object tracker)
    {
        var property = tracker.GetType().GetProperty(
            "CurrentEpoch", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(property is not null,
            $"{TrackerTypeName} exists but exposes no CurrentEpoch property. " + TrackerContract);
        Assert.Equal(typeof(long), property!.PropertyType);
        return (long)property.GetValue(tracker)!;
    }

    private static void EstablishInitialEpoch(object tracker)
    {
        var method = tracker.GetType().GetMethod(
            "EstablishInitialEpoch", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null, types: Type.EmptyTypes, modifiers: null);
        Assert.True(method is not null,
            $"{TrackerTypeName} exists but exposes no parameterless EstablishInitialEpoch(). " + TrackerContract);
        method!.Invoke(tracker, null);
    }

    private static IClipboardAuthorizationFreshness RequireFreshness(
        WebChallengeProof proof, WebChannelRegistry registry, object epochSource,
        IForegroundTargetCapture foregroundCapture, string requiredBehavior)
    {
        Assert.True(FreshnessType is not null, FreshnessContract + " REQUIRED FOR THIS TEST: " + requiredBehavior);
        object? instance = Activator.CreateInstance(FreshnessType!, proof, registry, epochSource, foregroundCapture);
        Assert.NotNull(instance);
        return Assert.IsAssignableFrom<IClipboardAuthorizationFreshness>(instance!);
    }

    private sealed record MatchingState(
        WebChannelRegistry Registry,
        long ChannelId,
        object Tracker,
        FakeClipboardForegroundTrigger Trigger,
        ScriptedForegroundTargetCapture Capture,
        WebChallengeProof Proof);

    /// <summary>
    /// Builds the fully-agreeing state every negative test below perturbs exactly one fact of: a
    /// real connected+asserted WebChannelRegistry channel, a real established epoch, a resolved
    /// foreground snapshot whose ProcessId is the channel's browser PID, and a proof bound to all
    /// four. Uses only real production types plus the two existing hand-written fakes.
    /// </summary>
    private static MatchingState BuildMatchingState(string requiredBehavior)
    {
        var trigger = new FakeClipboardForegroundTrigger();
        object tracker = RequireTracker(trigger, requiredBehavior);
        EstablishInitialEpoch(tracker);
        long epoch = CurrentEpoch(tracker);

        var registry = new WebChannelRegistry();
        Assert.True(registry.TryConnect(BrowserPid, out long channelId));
        Assert.True(registry.TryAssert(
            channelId, BrowserFocus.Focused, OriginResolution.Resolved, NeutralOrigin, out var evidence));

        var capture = new ScriptedForegroundTargetCapture
        {
            SnapshotToReturn = new ForegroundTargetSnapshot(
                IsResolved: true, ProcessId: BrowserPid, ProcessName: "browser-under-test"),
        };

        var proof = new WebChallengeProof(channelId, evidence.EvidenceRevision, epoch, BrowserPid);
        return new MatchingState(registry, channelId, tracker, trigger, capture, proof);
    }

    // ==================================================================
    // C2-R1 -- fully matching state authorizes
    // ==================================================================

    [Fact]
    public void C2_R1_AllBindingsAgree_IsStillCurrentReturnsTrue()
    {
        var state = BuildMatchingState(
            "a proof bound to a live channel, a matching evidence revision and PID, an established " +
            "epoch equal to the proof's, and a resolved foreground snapshot whose ProcessId equals " +
            "the proof's BrowserProcessId, with the epoch unchanged across the bracket, must return true.");

        var freshness = RequireFreshness(state.Proof, state.Registry, state.Tracker, state.Capture,
            "IsStillCurrent() == true for a fully-agreeing state.");

        Assert.True(freshness.IsStillCurrent());
        Assert.Equal(1, state.Capture.CaptureCallCount);

        // NO_MEMOIZATION -- a second call re-reads live state rather than replaying a cached answer.
        Assert.True(freshness.IsStillCurrent());
        Assert.Equal(2, state.Capture.CaptureCallCount);
    }

    // ==================================================================
    // C2-R2 -- one foreground event invalidates the bound proof
    // ==================================================================

    [Fact]
    public void C2_R2_ForegroundChangedEvent_AdvancesEpochAndInvalidatesOldProof()
    {
        var state = BuildMatchingState(
            "one accepted IClipboardForegroundTrigger.Changed event must increment the epoch exactly " +
            "once, and a proof bound to the previous epoch must then be rejected at step 6.");

        long before = CurrentEpoch(state.Tracker);
        state.Trigger.Raise();
        long after = CurrentEpoch(state.Tracker);

        Assert.Equal(before + 1, after);

        var freshness = RequireFreshness(state.Proof, state.Registry, state.Tracker, state.Capture,
            "IsStillCurrent() == false once the epoch has advanced past the proof's.");

        Assert.False(freshness.IsStillCurrent());

        // Cheap-terms-first ordering: the epoch mismatch is found at step 6, before any capture.
        Assert.Equal(0, state.Capture.CaptureCallCount);
    }

    [Fact]
    public void C2_R2_DuplicateForegroundEvents_EachIncrementOnce_NoDedupOrDebounce()
    {
        var trigger = new FakeClipboardForegroundTrigger();
        object tracker = RequireTracker(trigger,
            "every accepted Changed event increments exactly once -- no dedup, no debounce, no " +
            "coalescing, no HWND comparison (the trigger is structurally payload-free, so a " +
            "duplicate event is an intentional safe false negative).");

        EstablishInitialEpoch(tracker);
        long established = CurrentEpoch(tracker);

        trigger.Raise();
        trigger.Raise();
        trigger.Raise();

        Assert.Equal(established + 3, CurrentEpoch(tracker));
    }

    // ==================================================================
    // C2-R3 -- registry revision change invalidates
    // ==================================================================

    [Fact]
    public void C2_R3_RegistryInvalidate_AdvancesRevisionAndInvalidatesOldProof_WithoutCapturing()
    {
        var state = BuildMatchingState(
            "a WebChannelRegistry.TryInvalidate that advances EvidenceRevision must cause a proof " +
            "bound to the previous revision to be rejected at step 9, before any foreground capture.");

        Assert.True(state.Registry.TryInvalidate(state.ChannelId, out var newEvidence));
        Assert.NotEqual(state.Proof.EvidenceRevision, newEvidence.EvidenceRevision);

        var freshness = RequireFreshness(state.Proof, state.Registry, state.Tracker, state.Capture,
            "IsStillCurrent() == false after the channel's evidence revision advanced.");

        Assert.False(freshness.IsStillCurrent());
        Assert.Equal(0, state.Capture.CaptureCallCount);
    }

    // ==================================================================
    // C2-R4 -- disconnect invalidates
    // ==================================================================

    [Fact]
    public void C2_R4_ChannelDisconnected_RegistryLookupFails_WithoutCapturing()
    {
        var state = BuildMatchingState(
            "once the bound channel is disconnected, TryGetCurrentEvidence must fail (the entry is " +
            "removed outright, Gate 031F2.1) and IsStillCurrent() must return false at step 7, " +
            "before any foreground capture.");

        Assert.True(state.Registry.TryDisconnect(state.ChannelId));
        Assert.False(state.Registry.TryGetCurrentEvidence(state.ChannelId, out _));

        var freshness = RequireFreshness(state.Proof, state.Registry, state.Tracker, state.Capture,
            "IsStillCurrent() == false for a disconnected channel.");

        Assert.False(freshness.IsStillCurrent());
        Assert.Equal(0, state.Capture.CaptureCallCount);
    }

    // ==================================================================
    // C2-R5 -- reconnect issues a new ChannelId; the old proof stays dead
    // ==================================================================

    [Fact]
    public void C2_R5_ReconnectSamePid_IssuesNewChannelId_OldProofStillFalse()
    {
        var state = BuildMatchingState(
            "a disconnect followed by a reconnect of the SAME browser PID must allocate a strictly " +
            "new ChannelId (the allocator is monotonic and never reuses), and the old proof must " +
            "remain rejected -- it is never re-bound to the new channel automatically.");

        Assert.True(state.Registry.TryDisconnect(state.ChannelId));
        Assert.True(state.Registry.TryConnect(BrowserPid, out long reconnectedChannelId));
        Assert.NotEqual(state.ChannelId, reconnectedChannelId);
        Assert.True(reconnectedChannelId > state.ChannelId);

        Assert.True(state.Registry.TryAssert(
            reconnectedChannelId, BrowserFocus.Focused, OriginResolution.Resolved, NeutralOrigin, out _));

        var freshness = RequireFreshness(state.Proof, state.Registry, state.Tracker, state.Capture,
            "IsStillCurrent() == false for a proof bound to a retired ChannelId, even though a live " +
            "channel for the same browser PID now exists.");

        Assert.False(freshness.IsStillCurrent());
        Assert.Equal(0, state.Capture.CaptureCallCount);
    }

    // ==================================================================
    // C2-R6 -- current foreground PID mismatch
    // ==================================================================

    [Fact]
    public void C2_R6_ForegroundPidMismatch_ReturnsFalse()
    {
        var state = BuildMatchingState(
            "when every registry/epoch term agrees but the freshly captured foreground snapshot's " +
            "ProcessId differs from the proof's BrowserProcessId, IsStillCurrent() must return " +
            "false at step 13.");

        state.Capture.SnapshotToReturn = new ForegroundTargetSnapshot(
            IsResolved: true, ProcessId: BrowserPid + 1, ProcessName: "browser-under-test");

        var freshness = RequireFreshness(state.Proof, state.Registry, state.Tracker, state.Capture,
            "IsStillCurrent() == false when the live foreground PID is not the proof's browser PID.");

        Assert.False(freshness.IsStillCurrent());
        Assert.Equal(1, state.Capture.CaptureCallCount);
    }

    // ==================================================================
    // C2-R7 -- unresolved foreground
    // ==================================================================

    [Fact]
    public void C2_R7_UnresolvedForegroundSnapshot_ReturnsFalse()
    {
        var state = BuildMatchingState(
            "an unresolved (default) ForegroundTargetSnapshot must be rejected at step 12 -- " +
            "IsResolved is checked before ProcessId is ever trusted, so a default snapshot's " +
            "ProcessId of 0 can never be compared as if it were a real PID.");

        state.Capture.SnapshotToReturn = default;

        var freshness = RequireFreshness(state.Proof, state.Registry, state.Tracker, state.Capture,
            "IsStillCurrent() == false when the foreground cannot be resolved.");

        Assert.False(freshness.IsStillCurrent());
        Assert.Equal(1, state.Capture.CaptureCallCount);
    }

    // ==================================================================
    // C2-R8 / C2-R9 / C2-R10 -- zero-identifier fail-closed, short-circuited before any dependency
    // ==================================================================

    [Fact]
    public void C2_R8_ZeroProofChannelId_ReturnsFalse_WithoutConsultingRegistryOrCapture()
    {
        var state = BuildMatchingState(
            "proof.ChannelId == 0 must fail closed at step 1, before the registry is consulted and " +
            "before any foreground capture -- a zero identifier must never be allowed to reach an " +
            "equality comparison where zero could coincidentally match zero.");

        var zeroChannelProof = state.Proof with { ChannelId = 0 };

        var freshness = RequireFreshness(zeroChannelProof, state.Registry, state.Tracker, state.Capture,
            "IsStillCurrent() == false immediately for a zero ChannelId.");

        Assert.False(freshness.IsStillCurrent());
        Assert.Equal(0, state.Capture.CaptureCallCount);
    }

    [Fact]
    public void C2_R9_ZeroProofForegroundEpoch_ReturnsFalse_WithoutConsultingRegistryOrCapture()
    {
        var state = BuildMatchingState(
            "proof.ForegroundEpoch == 0 must fail closed at step 2. 0 is not an epoch -- it is the " +
            "fail-closed sentinel for unestablished/exhausted/disposed, so it must never authorize " +
            "even if the tracker itself were also reporting 0.");

        var zeroEpochProof = state.Proof with { ForegroundEpoch = 0 };

        var freshness = RequireFreshness(zeroEpochProof, state.Registry, state.Tracker, state.Capture,
            "IsStillCurrent() == false immediately for a zero ForegroundEpoch.");

        Assert.False(freshness.IsStillCurrent());
        Assert.Equal(0, state.Capture.CaptureCallCount);
    }

    [Fact]
    public void C2_R10_ZeroProofBrowserProcessId_ReturnsFalse_WithoutConsultingRegistryOrCapture()
    {
        var state = BuildMatchingState(
            "proof.BrowserProcessId == 0 must fail closed at step 3 -- 0 is never a valid PID, and " +
            "an unresolved foreground snapshot also carries ProcessId 0, so the two must never be " +
            "able to match each other.");

        var zeroPidProof = state.Proof with { BrowserProcessId = 0 };

        var freshness = RequireFreshness(zeroPidProof, state.Registry, state.Tracker, state.Capture,
            "IsStillCurrent() == false immediately for a zero BrowserProcessId.");

        Assert.False(freshness.IsStillCurrent());
        Assert.Equal(0, state.Capture.CaptureCallCount);
    }

    // ==================================================================
    // C2-R11 -- THE EPOCH BRACKET. Load-bearing: this test must fail if step 14/15 is removed.
    // ==================================================================

    [Fact]
    public void C2_R11_ForegroundChangesDuringCapture_EpochBracketRejects_EvenThoughPidStillMatches()
    {
        var state = BuildMatchingState(
            "FRESHNESS_EPOCH_BRACKET (Gate 031F4.1, REQUIRED): a foreground transition occurring " +
            "between the epochBefore read (step 4) and the epochAfter read (step 14) must force " +
            "false, EVEN WHEN every other term -- including the captured foreground PID -- still " +
            "agrees. This is the browser-window-A-to-window-B case: the epoch advances but the PID " +
            "does not change, so without the final epoch read the method would return a stale true " +
            "across a real foreground transition.");

        long boundEpoch = state.Proof.ForegroundEpoch;

        // Deterministic, zero-timing: the foreground event is raised synchronously from INSIDE
        // Capture(), so the epoch provably advances between step 4 and step 14 with no sleep and no
        // real thread race. The returned snapshot deliberately keeps the SAME matching PID.
        state.Capture.OnCapture = () => state.Trigger.Raise();

        var freshness = RequireFreshness(state.Proof, state.Registry, state.Tracker, state.Capture,
            "IsStillCurrent() == false when the epoch advances mid-call, despite a matching PID.");

        Assert.False(freshness.IsStillCurrent());

        // Proves the setup actually exercised the bracket rather than short-circuiting earlier:
        // the capture DID run (so steps 1-11 all passed), and the epoch DID advance during it.
        Assert.Equal(1, state.Capture.CaptureCallCount);
        Assert.Equal(boundEpoch + 1, CurrentEpoch(state.Tracker));
        Assert.Equal(boundEpoch, state.Proof.ForegroundEpoch);
    }

    // ==================================================================
    // C2-R12 -- EXCEPTION_OWNER split
    // ==================================================================

    [Fact]
    public void C2_R12_PartA_CaptureThrows_ExceptionPropagatesOutOfAppFreshness_NoCatchAll()
    {
        var state = BuildMatchingState(
            "EXCEPTION_OWNER = WINDOWS_BOUNDARY (Gate 031F4.1). The App freshness implementation " +
            "must contain NO catch-all: ordinary mechanical failure is already expressed as " +
            "false/bool by every dependency, so a genuinely exceptional condition must propagate " +
            "rather than be masked as an ordinary negative answer.");

        var sentinel = new InvalidOperationException("synthetic foreground capture failure");
        state.Capture.ExceptionToThrow = sentinel;

        var freshness = RequireFreshness(state.Proof, state.Registry, state.Tracker, state.Capture,
            "IsStillCurrent() propagates an unexpected capture exception instead of catching it.");

        var thrown = Assert.Throws<InvalidOperationException>(() => freshness.IsStillCurrent());
        Assert.Same(sentinel, thrown);
    }

    [Fact]
    public void C2_R12_PartB_WindowsBoundaryIsTheSingleCatchSite_AndIsImplementationAgnostic()
    {
        // The other half of the split is ALREADY GREEN (Phase C1, Gate 031F4): ClipboardChangeMonitor's
        // private static IsAuthorizationStillCurrent is the ONE call site of the interface and maps any
        // thrown exception to the same fail-closed outcome as a plain false. That behavior is proven
        // behaviorally, against a real guarded clipboard operation, by the three R4 exception tests in
        // Privon.Windows.IntegrationTests/Gate031D2_WebFreshnessSeamTests.cs -- which this gate must not
        // modify, and which are deliberately not duplicated here.
        //
        // Those R4 tests bind to the INTERFACE, never to any particular implementation, so they already
        // cover the future WebClipboardAuthorizationFreshness exactly as they cover the scripted test
        // double. Privon.App.Tests cannot itself drive a fake-injected ClipboardChangeMonitor -- the
        // native seams are internal to Privon.Windows and InternalsVisibleTo names only
        // Privon.Windows.IntegrationTests -- so this test asserts the structural half that IS reachable
        // from here: the single catch site exists, is private, static, and is not something the App
        // layer can bypass or duplicate.
        var catchSite = typeof(ClipboardChangeMonitor).GetMethod(
            "IsAuthorizationStillCurrent",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: [typeof(IClipboardAuthorizationFreshness)],
            modifiers: null);

        Assert.NotNull(catchSite);
        Assert.True(catchSite!.IsPrivate);
        Assert.Equal(typeof(bool), catchSite.ReturnType);

        // And Privon.App must never grow a competing catch-site of its own for this interface.
        var appImplementations = AppAssembly.GetTypes()
            .Where(t => typeof(IClipboardAuthorizationFreshness).IsAssignableFrom(t) && !t.IsInterface)
            .ToList();
        Assert.True(appImplementations.Count <= 1,
            "Privon.App must contain at most ONE IClipboardAuthorizationFreshness implementation " +
            "(WebClipboardAuthorizationFreshness). Found: " +
            string.Join(", ", appImplementations.Select(t => t.FullName)));
    }

    // ==================================================================
    // C2-R13 -- unknown channel, cheap-terms-first ordering
    // ==================================================================

    [Fact]
    public void C2_R13_UnknownChannelId_ReturnsFalse_WithoutCapturing()
    {
        var state = BuildMatchingState(
            "a nonzero ChannelId that the registry has never issued must be rejected at step 7, " +
            "with the foreground capture never invoked -- proving the frozen cheap-terms-first " +
            "ordering that keeps a potentially expensive Authenticode-bearing capture off the " +
            "rejection path.");

        var unknownChannelProof = state.Proof with { ChannelId = state.ChannelId + 9999 };

        var freshness = RequireFreshness(unknownChannelProof, state.Registry, state.Tracker, state.Capture,
            "IsStillCurrent() == false for an unknown ChannelId, with zero capture calls.");

        Assert.False(freshness.IsStillCurrent());
        Assert.Equal(0, state.Capture.CaptureCallCount);
    }

    // ==================================================================
    // C2-R14 -- exhaustion. Bounded and deterministic: private state is driven to long.MaxValue by
    // reflection, never by looping. No production test hook is added or required.
    // ==================================================================

    [Fact]
    public void C2_R14_EpochExhaustion_IsPermanentAndFailsClosed()
    {
        var trigger = new FakeClipboardForegroundTrigger();
        object tracker = RequireTracker(trigger,
            "the valid epoch domain is 1..long.MaxValue. Reaching long.MaxValue as a VALUE is legal " +
            "and usable; an attempt to advance BEYOND it must latch a permanent, sticky exhausted " +
            "state in which CurrentEpoch returns 0 forever. Never wraps, never goes negative, never " +
            "reuses an epoch. Further Changed events keep it 0, and EstablishInitialEpoch() must " +
            "not revive it.");

        EstablishInitialEpoch(tracker);
        Assert.True(CurrentEpoch(tracker) > 0);

        SetPrivateEpochCounter(tracker, long.MaxValue);
        Assert.Equal(long.MaxValue, CurrentEpoch(tracker));

        // The increment that would go beyond long.MaxValue latches exhaustion instead of wrapping.
        trigger.Raise();
        Assert.Equal(0, CurrentEpoch(tracker));

        // Permanent: neither further events nor EstablishInitialEpoch may revive it.
        trigger.Raise();
        trigger.Raise();
        Assert.Equal(0, CurrentEpoch(tracker));

        EstablishInitialEpoch(tracker);
        Assert.Equal(0, CurrentEpoch(tracker));
    }

    [Fact]
    public void C2_R14_ExhaustedTracker_MakesFreshnessFailClosed()
    {
        var state = BuildMatchingState(
            "an exhausted tracker reports CurrentEpoch 0, which can never equal any valid proof " +
            "epoch (always > 0), so IsStillCurrent() fails closed at step 5/6 by construction -- " +
            "no separate exhaustion branch is needed anywhere in the freshness object.");

        SetPrivateEpochCounter(state.Tracker, long.MaxValue);
        state.Trigger.Raise();
        Assert.Equal(0, CurrentEpoch(state.Tracker));

        var freshness = RequireFreshness(state.Proof, state.Registry, state.Tracker, state.Capture,
            "IsStillCurrent() == false against an exhausted epoch tracker, with zero capture calls.");

        Assert.False(freshness.IsStillCurrent());
        Assert.Equal(0, state.Capture.CaptureCallCount);
    }

    /// <summary>
    /// EXHAUSTION_TESTABILITY (Gate 031F4.2): drives the tracker's own private counter to
    /// long.MaxValue by reflection rather than by looping long.MaxValue times, and WITHOUT adding
    /// any public production setter or counter-injection API. Identifies the counter as the type's
    /// single private instance field of type long -- if a future implementation carries more than
    /// one, this fails with an explicit message rather than guessing.
    /// </summary>
    private static void SetPrivateEpochCounter(object tracker, long value)
    {
        var longFields = tracker.GetType()
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(f => f.FieldType == typeof(long))
            .ToList();

        Assert.True(longFields.Count == 1,
            $"{TrackerTypeName} must carry exactly ONE private long instance field (its epoch " +
            $"counter) so the exhaustion boundary is reachable in tests without adding a public " +
            $"production test hook. Found {longFields.Count}: " +
            string.Join(", ", longFields.Select(f => f.Name)));

        longFields[0].SetValue(tracker, value);
    }

    // ==================================================================
    // C2-R15 -- disposal
    // ==================================================================

    [Fact]
    public void C2_R15_DisposedTracker_ReportsZeroEpoch_UnsubscribesAndNeverRevives()
    {
        var trigger = new FakeClipboardForegroundTrigger();
        object tracker = RequireTracker(trigger,
            "Dispose() must unsubscribe from IClipboardForegroundTrigger.Changed and make " +
            "CurrentEpoch report 0 permanently -- a disposed tracker can no longer observe " +
            "foreground changes, so its epoch is no longer trustworthy and must fail closed by the " +
            "same 0-sentinel mechanism as exhaustion. EstablishInitialEpoch() must not revive it.");

        EstablishInitialEpoch(tracker);
        trigger.Raise();
        long established = CurrentEpoch(tracker);
        Assert.True(established > 0);

        var disposable = Assert.IsAssignableFrom<IDisposable>(tracker);
        disposable.Dispose();

        Assert.Equal(0, CurrentEpoch(tracker));

        // Unsubscribed: a later event neither increments nor revives.
        trigger.Raise();
        Assert.Equal(0, CurrentEpoch(tracker));

        EstablishInitialEpoch(tracker);
        Assert.Equal(0, CurrentEpoch(tracker));
    }

    [Fact]
    public void C2_R15_TrackerDispose_IsIdempotent()
    {
        // REPOSITORY CONVENTION (audited, not invented): "idempotent-on-success Dispose" is the
        // established discipline across ClipboardChangeMonitor, ComposerTextReader,
        // ForegroundChangeMonitor and SessionLockMonitor. Those four qualify it with
        // DISPOSE_FAILURE_BEHAVIOR only because they own native resources whose cleanup can fail.
        // ForegroundEpochTracker owns no native resource -- it only unsubscribes an event handler --
        // so the unqualified form of the same convention applies: repeated Dispose is a silent no-op.
        var trigger = new FakeClipboardForegroundTrigger();
        object tracker = RequireTracker(trigger,
            "Dispose() is idempotent -- calling it repeatedly is a safe no-op that never throws.");

        EstablishInitialEpoch(tracker);
        var disposable = Assert.IsAssignableFrom<IDisposable>(tracker);

        disposable.Dispose();
        disposable.Dispose();
        disposable.Dispose();

        Assert.Equal(0, CurrentEpoch(tracker));
    }

    [Fact]
    public void C2_R15_DisposedTracker_MakesFreshnessFailClosed()
    {
        var state = BuildMatchingState(
            "a disposed tracker reports 0, so IsStillCurrent() must fail closed at step 5 with no " +
            "registry lookup and no foreground capture.");

        Assert.IsAssignableFrom<IDisposable>(state.Tracker).Dispose();

        var freshness = RequireFreshness(state.Proof, state.Registry, state.Tracker, state.Capture,
            "IsStillCurrent() == false against a disposed epoch tracker.");

        Assert.False(freshness.IsStillCurrent());
        Assert.Equal(0, state.Capture.CaptureCallCount);
    }

    // ==================================================================
    // C2-R16 -- structural privacy / policy separation
    // ==================================================================

    // Exact, whole-token forbidden values. Deliberately NOT substring-matched against member names
    // the way an over-broad list would be: Gate 031F3.1's own "Browser" false positive (which
    // wrongly flagged the legitimate frozen field name BrowserProcessId) is the recorded precedent
    // for why this list contains only real product/publisher/browser-binary/origin values and is
    // applied to member names with exact-token care below.
    private static readonly string[] ForbiddenProductValues =
    [
        "ChatGPT", "Claude", "Gemini", "Grok", "DeepSeek",
        "chatgpt.com", "claude.ai", "gemini.google.com", "grok.com", "chat.deepseek.com",
        "Google LLC", "Microsoft Corporation", "chrome", "msedge",
    ];

    [Fact]
    public void C2_R16_MechanicalAppFreshnessSources_ContainNoProductPolicyValues()
    {
        Assert.True(TrackerType is not null && FreshnessType is not null,
            "Both Privon.App.ForegroundEpochTracker and Privon.App.WebClipboardAuthorizationFreshness " +
            "must exist and must contain ZERO occurrences of any supported product, origin, " +
            "publisher, or browser-binary value -- those remain WebTargetGate policy exclusively. " +
            TrackerContract);

        foreach (var type in new[] { TrackerType!, FreshnessType! })
        {
            // Member names.
            var offendingMembers = type
                .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                    | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Select(m => m.Name)
                .Where(name => ForbiddenProductValues.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            Assert.Empty(offendingMembers);

            // Constant/literal values -- a policy string smuggled in as a const is the real hazard.
            var offendingConstants = type
                .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .Select(f => (string?)f.GetRawConstantValue())
                .Where(v => v is not null && ForbiddenProductValues.Any(
                    f => v.Contains(f, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            Assert.Empty(offendingConstants);
        }
    }

    // ------------------------------------------------------------------
    // SOURCE-LEVEL hygiene lock (Gate 031F4.2.1 section 2). Reflection can only see member names and
    // constant VALUES -- it structurally cannot see comments or XML documentation, and Gate 031F3.1
    // already proved that forbidden product strings do reach this codebase through doc comments
    // (three Privon.Browser files had to be genericized for exactly that reason). The reflection
    // checks above are retained as-is and this test adds the half they cannot cover: a scan of the
    // ENTIRE source file text of each mechanical type.
    // ------------------------------------------------------------------

    /// <summary>
    /// Locates the source file for a Privon.App type using this repository's own strict
    /// one-public-type-per-file, file-named-after-the-type convention (ForegroundTargetCapture.cs,
    /// ClipboardForegroundTrigger.cs, WebTargetGate.cs, WebDecisionContext.cs, ... -- every single
    /// file in src/Privon.App follows it). The repository root is found by walking up from the test
    /// assembly's own location until the solution file is seen -- no line numbers, no hardcoded
    /// absolute path, and no production marker added anywhere just to make discovery easy.
    /// </summary>
    private static string? TryFindAppSourceFile(string typeSimpleName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PRIVON.slnx")))
            directory = directory.Parent;

        if (directory is null)
            return null;

        string candidate = Path.Combine(directory.FullName, "src", "Privon.App", typeSimpleName + ".cs");
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// RED_INTEGRITY guard for the source-hygiene lock below. The three scans are currently RED, and
    /// this test is what proves they are RED because the Phase-C2 source files genuinely do not exist
    /// -- NOT because the discovery walk is broken and silently returning null for everything (which
    /// would be a test defect wearing an expected-RED costume). It resolves a type that certainly
    /// does exist, proves the returned path is real, and proves the scan has actual teeth by
    /// confirming that WebTargetGate -- the one type that legitimately OWNS product policy -- does
    /// contain the very values the mechanical types must never contain.
    /// </summary>
    [Fact]
    public void C2_R16_SourceDiscovery_Works_AndTheScanHasTeeth()
    {
        string? policyOwnerPath = TryFindAppSourceFile(nameof(WebTargetGate));

        Assert.True(policyOwnerPath is not null,
            "The repository-root walk used by the source-hygiene lock failed to locate a source file " +
            "that definitely exists (src/Privon.App/WebTargetGate.cs). Fix the discovery technique " +
            "before trusting any RED it reports for the not-yet-implemented Phase-C2 files.");

        string policyOwnerSource = File.ReadAllText(policyOwnerPath!);

        // WebTargetGate is the sole legitimate owner of this policy, so its source MUST trip the exact
        // scan the mechanical types must pass. If this ever came back empty, the scan below would be
        // vacuous and every future GREEN it reports would be meaningless.
        var hits = ForbiddenProductValues
            .Where(f => policyOwnerSource.Contains(f, StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(hits);
    }

    [Theory]
    [InlineData("ForegroundEpochTracker")]
    [InlineData("IForegroundEpochSource")]
    [InlineData("WebClipboardAuthorizationFreshness")]
    public void C2_R16_MechanicalAppFreshnessSourceFiles_ContainNoForbiddenValueAnywhere_IncludingComments(
        string typeSimpleName)
    {
        string? sourcePath = TryFindAppSourceFile(typeSimpleName);

        Assert.True(sourcePath is not null,
            $"EXPECTED_C2_RED: src/Privon.App/{typeSimpleName}.cs does not exist yet -- the mechanical " +
            "Phase-C2 type it will contain has not been implemented. FROZEN SOURCE-HYGIENE RULE for " +
            "that file once it lands (Gate 031F4.2.1 section 2): ZERO occurrences of ChatGPT, Claude, " +
            "Gemini, Grok, DeepSeek, chatgpt.com, claude.ai, gemini.google.com, grok.com, " +
            "chat.deepseek.com, Google LLC, Microsoft Corporation, chrome, or msedge -- anywhere in " +
            "the file, INCLUDING comments, XML documentation, examples, exception text, and string " +
            "literals, not merely in member names and constants. Those values remain WebTargetGate " +
            "policy exclusively. " + TrackerContract);

        string source = File.ReadAllText(sourcePath!);

        // Case-sensitive: the frozen rule verbatim.
        var caseSensitiveHits = ForbiddenProductValues
            .Where(f => source.Contains(f, StringComparison.Ordinal))
            .ToList();
        Assert.Empty(caseSensitiveHits);

        // Case-insensitive audit, to also catch a casing-variant slipped into prose.
        // "Grok" is deliberately EXCLUDED from this pass only: lowercase "grok" is an ordinary English
        // verb, so a case-insensitive rule for it would be an over-broad token of exactly the kind
        // Gate 031F3.1's "Browser" false positive warns against. The product name itself is still
        // fully covered by the case-sensitive pass above.
        var caseInsensitiveHits = ForbiddenProductValues
            .Where(f => !string.Equals(f, "Grok", StringComparison.Ordinal))
            .Where(f => source.Contains(f, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Empty(caseInsensitiveHits);
    }

    [Fact]
    public void C2_R16_WebClipboardAuthorizationFreshness_ExposesNoOriginLikeMember()
    {
        Assert.True(FreshnessType is not null,
            "Privon.App.WebClipboardAuthorizationFreshness must expose no Origin/Url/Uri/Domain/Host " +
            "member of any kind -- the frozen IsStillCurrent algorithm never needs an origin, and " +
            "the object retains neither Origin nor the full WebForegroundEvidence. " + FreshnessContract);

        var forbidden = new[] { "Origin", "Url", "Uri", "Domain", "Host" };

        var offending = FreshnessType!
            .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .Where(name => forbidden.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(offending);
    }

    [Fact]
    public void C2_R16_WebClipboardAuthorizationFreshness_RetainsOnlyTheBoundProofAndDependencies()
    {
        Assert.True(FreshnessType is not null,
            "The freshness object must retain ONLY the bound WebChallengeProof and its three " +
            "dependency references -- never a WebForegroundEvidence, a SupportedWebTarget, a " +
            "ForegroundTargetSnapshot, a string, or any timestamp. " + FreshnessContract);

        var forbiddenFieldTypes = new[]
        {
            typeof(WebForegroundEvidence), typeof(SupportedWebTarget), typeof(ForegroundTargetSnapshot),
            typeof(string), typeof(DateTime), typeof(DateTimeOffset), typeof(TimeSpan),
        };

        var offending = FreshnessType!
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(f => forbiddenFieldTypes.Contains(Nullable.GetUnderlyingType(f.FieldType) ?? f.FieldType))
            .Select(f => $"{f.FieldType.Name} {f.Name}")
            .ToList();

        Assert.Empty(offending);
    }

    [Fact]
    public void C2_R16_ForegroundEpochSourceSeam_IsMinimalAndSynchronous()
    {
        Assert.True(EpochSourceInterface is not null,
            "Privon.App.IForegroundEpochSource must exist as the narrow seam the freshness object " +
            "depends on (mirroring IForegroundTargetCapture's own narrow-seam pattern), exposing " +
            "exactly one synchronous member: long CurrentEpoch { get; }. No Task, no async, no " +
            "setter, no event, no Web/product terminology. " + TrackerContract);

        var members = EpochSourceInterface!.GetMembers();
        var properties = EpochSourceInterface.GetProperties();

        Assert.Single(properties);
        Assert.Equal("CurrentEpoch", properties[0].Name);
        Assert.Equal(typeof(long), properties[0].PropertyType);
        Assert.True(properties[0].CanRead);
        Assert.False(properties[0].CanWrite);

        // Only the property and its getter -- no additional methods/events smuggled onto the seam.
        Assert.DoesNotContain(members, m => m is MethodInfo method && !method.IsSpecialName);
        Assert.Empty(EpochSourceInterface.GetEvents());
    }

    // ==================================================================
    // C2-R17 -- subscription-order trip-wire
    // ==================================================================

    [Fact]
    public void C2_R17_TrackerSubscribesInItsOwnConstructor_SoLaterSubscribersSeeTheAdvancedEpoch()
    {
        // TRACKER_SUBSCRIBES_FIRST (Gate 031F4.1). The tracker and the coordinator both subscribe to
        // the same payload-free multicast event and both handlers run synchronously on the monitor's
        // single owner thread, so invocation order IS `+=` order. The behavioral half that is fully
        // provable today: the tracker subscribes inside its OWN constructor, so ANY handler attached
        // after construction observes an already-advanced epoch. Combined with the composition root
        // constructing the tracker before the coordinator, that yields the required guarantee.
        var trigger = new FakeClipboardForegroundTrigger();
        object tracker = RequireTracker(trigger,
            "ForegroundEpochTracker must subscribe to IClipboardForegroundTrigger.Changed inside its " +
            "own constructor, so that every handler subscribed later -- notably " +
            "ClipboardPrivacyCoordinator.OnForegroundChanged, which the composition root attaches " +
            "afterwards -- observes an epoch that has ALREADY advanced for that same event.");

        EstablishInitialEpoch(tracker);
        long before = CurrentEpoch(tracker);

        // A stand-in for a later subscriber (the coordinator's own handler in production).
        long observedByLaterSubscriber = -1;
        trigger.Changed += (_, _) => observedByLaterSubscriber = CurrentEpoch(tracker);

        trigger.Raise();

        Assert.Equal(before + 1, CurrentEpoch(tracker));
        Assert.Equal(before + 1, observedByLaterSubscriber);
    }

    [Fact]
    public async Task C2_R17_TrackerAdvancesBeforeCoordinatorHandlingObservesTheSameEvent()
    {
        // Gate 031F4.3 section 20 -- the strongest NON-BRITTLE behavioral proof available without
        // adding any production test-only API. No line numbers, no source-text ordering, no private
        // field inspection: this drives the REAL ForegroundEpochTracker and the REAL
        // ClipboardPrivacyCoordinator over one shared trigger, in the exact order the composition
        // root wires them, and observes one real foreground event.
        var trigger = new FakeClipboardForegroundTrigger();

        // (1) tracker constructed first -- it subscribes in its own constructor.
        using var tracker = new ForegroundEpochTracker(trigger);
        tracker.EstablishInitialEpoch();
        long before = tracker.CurrentEpoch;
        Assert.True(before > 0);

        // (2) coordinator constructed and started second -- it subscribes during Start.
        var targetCapture = new FakeForegroundTargetCapture();
        var coordinator = new ClipboardPrivacyCoordinator(
            new FakeClipboardReadTransport(),
            targetCapture,
            new FakeClipboardPrivacyProcessor(),
            new FakeClipboardWriteTransport(),
            new FakeClipboardNotificationLifecycle(),
            new FakeClipboardDecisionSessionPublisher(),
            new ClipboardOperationGate(),
            new FakeClipboardComposerVerificationHandoff(),
            new FakeClipboardComposerVerificationInvalidation(),
            foregroundTrigger: trigger);
        coordinator.Start();

        // (3) a probe subscribed LAST, standing in for any handler attached after the tracker --
        // which the coordinator provably is, having subscribed only at Start above.
        long observedByLaterSubscriber = -1;
        trigger.Changed += (_, _) => observedByLaterSubscriber = tracker.CurrentEpoch;

        trigger.Raise();

        // The tracker's advance completed SYNCHRONOUSLY, before Raise() returned -- so every
        // subsequent observation of the epoch, by any subscriber or by any work those subscribers
        // enqueue, necessarily sees the post-transition value.
        Assert.Equal(before + 1, tracker.CurrentEpoch);
        Assert.Equal(before + 1, observedByLaterSubscriber);

        // ...and the coordinator genuinely handled that SAME event (rather than ignoring it), so the
        // ordering proven above is ordering against a real, live coordinator subscription.
        await WaitUntilAsync(() => targetCapture.CaptureCallCount >= 1);
        Assert.Equal(before + 1, tracker.CurrentEpoch);

        coordinator.Stop();
        coordinator.Dispose();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not met within the timeout.");
            await Task.Delay(10);
        }
    }

    // COMPOSITION-ROOT HALF -- deliberately NO TEST (Gate 031F4.2.1 section 3).
    //
    // Classified in the gate report as EXPECTED_DEFERRED_COMPOSITION_RED. The frozen contract requires
    // only that (a) the tracker subscribes before the coordinator and (b) the composition root owns
    // its lifetime/disposal. It does NOT freeze PrivonAppComposition's private storage
    // representation, so an interim assertion about a concrete private field -- its existence, its
    // type, or its count -- would be STRONGER than the contract and would wrongly constrain the
    // implementation. Gate 031F4.2's version of this test did exactly that and has been removed
    // rather than weakened.
    //
    // No fake assertion stands in for it while the production wiring does not exist. The behavioral
    // half that IS provable today is the constructor-subscription test directly above.
    //
    // FUTURE REPLACEMENT CRITERION (frozen): once composition wiring lands, add a BEHAVIORAL ordering
    // test proving that one foreground Changed event advances the tracker BEFORE the coordinator's own
    // foreground-change handling/enqueue observes that same event. Never a source line-number test,
    // and never a concrete-field-count test.
}
