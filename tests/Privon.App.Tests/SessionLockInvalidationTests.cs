using Privon.App;
using Privon.Core;
using Privon.Detection;
using Privon.Windows;

namespace Privon.App.Tests;

// Phase 3C STEP38 -- App-level session-lock POLICY EFFECT regression: what actually happens to
// REAL lifecycle/verifier/scope/grant/pending state when PrivonAppComposition's own session-lock
// callback fires. Uses the REAL production ClipboardDecisionScopeLifecycle/ClipboardComposerVerifier/
// ClipboardOperationGate wired by a REAL PrivonAppComposition -- the ONE documented, justified
// exception in this test suite is ISessionLockNotification itself: raising a real
// Privon.Windows.SessionLockMonitor.Locked event requires actual Windows session-lock delivery,
// which no automated test may trigger (Phase 3C STEP37/STEP38, frozen) -- so FakeSessionLockNotification
// stands in for it, wired through PrivonAppComposition's own real, unmodified callback-subscription
// logic exactly as a real SessionLockNotificationAdapter would be. See SessionLockMonitorTests.cs
// (Privon.Windows.IntegrationTests) for the separate, real-native-seam-backed proof that
// SessionLockMonitor itself correctly translates WTS_SESSION_LOCK into Locked and nothing else.
public class SessionLockInvalidationTests
{
    private static string CreateTempStorageRoot() =>
        Path.Combine(Path.GetTempPath(), "SessionLockInvalidationTests", Guid.NewGuid().ToString("N"));

    private static (PrivonAppComposition Composition, FakeSessionLockNotification SessionLock) CreateStarted()
    {
        var sessionLock = new FakeSessionLockNotification();
        var composition = new PrivonAppComposition(
            CreateTempStorageRoot(), sessionLock, new ClipboardChangeMonitor(), new ComposerTextReader(), new ForegroundChangeMonitor());
        composition.Start();
        return (composition, sessionLock);
    }

    private static object GetField(object obj, string name)
    {
        var field = obj.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
            ?? throw new InvalidOperationException($"Field '{name}' not found on {obj.GetType()}.");
        return field.GetValue(obj) ?? throw new InvalidOperationException($"Field '{name}' was null.");
    }

    // ==================================================================
    // CALLBACK WIRING / ORDERING (item 23)
    // ==================================================================

    [Fact]
    public void Lock_AdvancesLifecycleGeneration()
    {
        var (composition, sessionLock) = CreateStarted();
        using var _ = composition;

        var lifecycle = (ClipboardDecisionScopeLifecycle)GetField(composition, "_lifecycle");
        var generationBefore = lifecycle.CurrentGeneration;

        sessionLock.RaiseLocked();

        Assert.True(lifecycle.CurrentGeneration > generationBefore, "Reset() must advance the generation.");
    }

    [Fact]
    public async Task Lock_DoesNotAcquireOperationGate_EvenWhileGateIsHeldByAnotherOperation()
    {
        var (composition, sessionLock) = CreateStarted();
        using var _ = composition;

        var gate = (ClipboardOperationGate)GetField(composition, "_operationGate");
        await gate.WaitAsync(); // acquire and hold the single permit -- never released until this test ends
        try
        {
            var lifecycle = (ClipboardDecisionScopeLifecycle)GetField(composition, "_lifecycle");
            var generationBefore = lifecycle.CurrentGeneration;

            // OnSessionLocked is a plain synchronous `void` handler -- it is structurally incapable
            // of awaiting anything (a synchronous method cannot await). This call completing and
            // producing its effect while the gate is still fully held by this test is the runtime
            // confirmation of that structural fact -- if it had somehow needed the gate, this exact
            // call would still be blocked right now.
            sessionLock.RaiseLocked();

            Assert.True(lifecycle.CurrentGeneration > generationBefore);
        }
        finally
        {
            gate.Release();
        }
    }

    [Fact]
    public void Lock_RepeatedEvents_AreSafe_GenerationKeepsAdvancing_NoDedupOrDebounce()
    {
        var (composition, sessionLock) = CreateStarted();
        using var _ = composition;

        var lifecycle = (ClipboardDecisionScopeLifecycle)GetField(composition, "_lifecycle");
        var g0 = lifecycle.CurrentGeneration;

        sessionLock.RaiseLocked();
        var g1 = lifecycle.CurrentGeneration;
        sessionLock.RaiseLocked();
        var g2 = lifecycle.CurrentGeneration;
        sessionLock.RaiseLocked();
        var g3 = lifecycle.CurrentGeneration;

        Assert.True(g1 > g0);
        Assert.True(g2 > g1);
        Assert.True(g3 > g2);
    }

    // ==================================================================
    // ACTIVE SCOPE / GRANT INVALIDATION (item 24)
    // ==================================================================

    [Fact]
    public void Lock_DeactivatesActiveDecisionScope_AndItsBypassGrantsBecomeUnreachable()
    {
        var (composition, sessionLock) = CreateStarted();
        using var _ = composition;

        var lifecycle = (ClipboardDecisionScopeLifecycle)GetField(composition, "_lifecycle");

        // Build a real, published, fully-resolved (BypassOnce) decision scope -- exactly the shape
        // ClipboardDecisionSessionPublisher/ClipboardDecisionActionResolver would produce in
        // production, using only already-public seams (no new test-only API on the scope).
        var g1 = lifecycle.AdvanceOnClipboardNotification();
        var publisher = new ClipboardDecisionSessionPublisher(lifecycle);
        var item = new ClipboardDecisionItem(new CanonicalValue(PiiType.Phone, "01099998888"), RiskLevel.Level1);
        var published = publisher.TryPublish(g1, "raw text mentioning 01099998888", new ClipboardDecisionPlan([item]));
        Assert.True(published);

        var scope = lifecycle.GetActiveScope()!;
        scope.ApplyIntent(item, ClipboardDecisionIntent.BypassOnce);
        scope.CommitResolved(scope.InitialStamp);
        Assert.True(scope.HasBypassGrant(scope.InitialStamp, item.Canonical));
        Assert.True(lifecycle.IsActive(scope));

        sessionLock.RaiseLocked();

        Assert.False(lifecycle.IsActive(scope), "The pre-lock scope must no longer be the active scope.");
        Assert.Null(lifecycle.GetActiveScope());
        // The grant object itself still technically exists on the now-detached `scope` local
        // (STALE_OBJECT_MUTATION_POLICY, Phase 3B STEP24.1) -- what matters, and is what this proves,
        // is that it is no longer reachable through the authoritative active lifecycle at all.
    }

    // ==================================================================
    // PENDING COMPOSER VERIFICATION INVALIDATION (item 25)
    // ==================================================================

    [Fact]
    public async Task Lock_ClearsPendingComposerVerification_NextVerifyReturnsNoPendingOutcome()
    {
        var (composition, sessionLock) = CreateStarted();
        using var _ = composition;

        var lifecycle = (ClipboardDecisionScopeLifecycle)GetField(composition, "_lifecycle");
        var verifier = composition.Verifier!;
        var handoff = (IClipboardComposerVerificationHandoff)verifier;

        var target = new ForegroundTargetSnapshot(IsResolved: true, ProcessId: 4242, ProcessName: "ChatGPT");
        var currentGeneration = lifecycle.CurrentGeneration;
        var published = handoff.Publish(target, "SYNTHETIC-EXPECTED-PROTECTED-TEXT", currentGeneration);
        Assert.True(published);

        sessionLock.RaiseLocked();

        // No pending remains -- VerifyAsync's own first check (pinned is null) short-circuits before
        // any composer read is even attempted.
        var result = await verifier.VerifyAsync();
        Assert.Equal(VerificationOutcome.NotAttempted, result.Outcome);
    }
}
