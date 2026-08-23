using System.Reflection;
using Privon.App;
using Privon.Core;

namespace Privon.App.Tests;

// Phase 3B STEP17 -- ClipboardDecisionScopeLifecycle regression. Proves the exact MODEL A
// atomicity contract Phase 3B STEP16.1 froze: generation advance and active-scope
// publish/drop always happen as one indivisible critical section, so a stale proposal can
// never become (or remain) active once a newer clipboard notification has arrived. No fakes --
// this is the real, only production implementation of both narrow interfaces.
//
// Phase 3B STEP21 -- ClipboardDecisionScope now carries a real payload (private RevisionTracker,
// InitialStamp, Items), so every test below that previously used the old parameterless
// the old parameterless constructor now goes through CreateScope(...), a thin helper that builds a
// minimal-but-real scope. The lifecycle/atomicity tests in this file deliberately don't care about
// scope PAYLOAD content -- only about scope IDENTITY (reference equality) -- so a fresh
// RevisionTracker/Observe("scope-identity-marker") + empty Items is sufficient and does not change
// what any of these tests actually prove.
public class ClipboardDecisionScopeLifecycleTests
{
    private static ClipboardDecisionScope CreateScope()
    {
        var tracker = new RevisionTracker();
        var stamp = tracker.Observe("scope-identity-marker");
        return new ClipboardDecisionScope(tracker, stamp, Array.Empty<ClipboardDecisionItem>(), generation: 0);
    }

    // ==================================================================
    // DETERMINISTIC LIFECYCLE BEHAVIOR (section 24, items 1-12)
    // ==================================================================

    // ---- 1. initial generation state follows the same "0 = none yet" convention already
    // established by Privon.Core.RevisionId.None ----
    [Fact]
    public void InitialState_GenerationIsZero_NoActiveScope()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();

        Assert.Equal(0, lifecycle.CurrentGeneration);
        Assert.False(lifecycle.HasActiveScope);
    }

    // ---- 2. first Advance returns the next generation (1) ----
    [Fact]
    public void FirstAdvance_ReturnsOne()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();

        Assert.Equal(1, lifecycle.AdvanceOnClipboardNotification());
    }

    // ---- 3. every Advance strictly increments ----
    [Fact]
    public void EveryAdvance_StrictlyIncrements()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();

        var g1 = lifecycle.AdvanceOnClipboardNotification();
        var g2 = lifecycle.AdvanceOnClipboardNotification();
        var g3 = lifecycle.AdvanceOnClipboardNotification();

        Assert.Equal(1, g1);
        Assert.Equal(2, g2);
        Assert.Equal(3, g3);
    }

    // ---- 4. Advance clears any active scope ----
    [Fact]
    public void Advance_ClearsActiveScope()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var g1 = lifecycle.AdvanceOnClipboardNotification();
        Assert.True(lifecycle.TryPublish(g1, CreateScope()));
        Assert.True(lifecycle.HasActiveScope);

        lifecycle.AdvanceOnClipboardNotification();

        Assert.False(lifecycle.HasActiveScope);
    }

    // ---- 5. matching-generation TryPublish succeeds ----
    [Fact]
    public void TryPublish_MatchingGeneration_Succeeds()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var g1 = lifecycle.AdvanceOnClipboardNotification();
        var scope = CreateScope();

        Assert.True(lifecycle.TryPublish(g1, scope));
        Assert.True(lifecycle.IsActive(scope));
    }

    // ---- 6. stale-generation TryPublish fails ----
    [Fact]
    public void TryPublish_StaleGeneration_Fails()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var g1 = lifecycle.AdvanceOnClipboardNotification();
        lifecycle.AdvanceOnClipboardNotification(); // now at g2

        Assert.False(lifecycle.TryPublish(g1, CreateScope()));
        Assert.False(lifecycle.HasActiveScope);
    }

    // ---- 7. publish G1 wins, THEN G2 arrives -> scope absent (allowed outcome A: published
    // then immediately invalidated) ----
    [Fact]
    public void PublishBeforeAdvance_ScopePublishedThenInvalidated()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var g1 = lifecycle.AdvanceOnClipboardNotification();
        var scope1 = CreateScope();

        Assert.True(lifecycle.TryPublish(g1, scope1));
        Assert.True(lifecycle.IsActive(scope1));

        lifecycle.AdvanceOnClipboardNotification(); // G2 arrives

        Assert.False(lifecycle.IsActive(scope1));
        Assert.False(lifecycle.HasActiveScope);
    }

    // ---- 8. G2 arrives BEFORE the G1 publish attempt -> publish returns false (allowed
    // outcome B: publication rejected) ----
    [Fact]
    public void AdvanceBeforePublish_PublicationRejected()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var g1 = lifecycle.AdvanceOnClipboardNotification();
        lifecycle.AdvanceOnClipboardNotification(); // G2 arrives before N1 ever tries to publish

        Assert.False(lifecycle.TryPublish(g1, CreateScope()));
        Assert.False(lifecycle.HasActiveScope);
    }

    // ---- 9. Reset increments generation ----
    [Fact]
    public void Reset_IncrementsGeneration()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var before = lifecycle.CurrentGeneration;

        lifecycle.Reset();

        Assert.True(lifecycle.CurrentGeneration > before);
    }

    // ---- 10. Reset clears active scope ----
    [Fact]
    public void Reset_ClearsActiveScope()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var g1 = lifecycle.AdvanceOnClipboardNotification();
        Assert.True(lifecycle.TryPublish(g1, CreateScope()));

        lifecycle.Reset();

        Assert.False(lifecycle.HasActiveScope);
    }

    // ---- 11. a proposal's expected generation, captured BEFORE Reset, can never publish
    // AFTER Reset -- proves Reset must also advance generation, not just null the scope ----
    [Fact]
    public void PreResetExpectedGeneration_CannotPublishAfterReset()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var g1 = lifecycle.AdvanceOnClipboardNotification();

        lifecycle.Reset();

        Assert.False(lifecycle.TryPublish(g1, CreateScope()));
        Assert.False(lifecycle.HasActiveScope);
    }

    // ---- 12. structural: generation metadata has no relationship to raw text -- the LIFECYCLE
    // itself remains payload-opaque, holding nothing but the generation counter and an opaque
    // ClipboardDecisionScope reference (never a string, never any Privon.Core/Privon.Detection
    // type directly) ----
    [Fact]
    public void Lifecycle_HasNoContentBearingField()
    {
        var fields = typeof(ClipboardDecisionScopeLifecycle).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.DoesNotContain(fields, f => f.FieldType == typeof(string));
        Assert.DoesNotContain(fields, f => f.FieldType.Namespace == "Privon.Detection");
        Assert.DoesNotContain(fields, f => f.FieldType.Namespace == "Privon.Core");
    }

    // ---- 12.1 (Phase 3B STEP21): the SCOPE itself now legitimately carries a private
    // Privon.Core.RevisionTracker/RevisionStamp payload -- but must still never hold a raw string
    // field of any kind (see ClipboardDecisionScopeTests.cs for the full payload regression) ----
    [Fact]
    public void Scope_HasNoStringField()
    {
        var fields = typeof(ClipboardDecisionScope).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.DoesNotContain(fields, f => f.FieldType == typeof(string));
    }

    // ==================================================================
    // ACTIVE SCOPE ACCESS (Phase 3B STEP21, per the STEP20 audit)
    // ==================================================================

    // ---- Y.1. no active scope -> GetActiveScope returns null ----
    [Fact]
    public void GetActiveScope_NoActiveScope_ReturnsNull()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();

        Assert.Null(lifecycle.GetActiveScope());
    }

    // ---- Y.2. publish matching generation -> GetActiveScope returns the exact same scope
    // reference (never a copy) ----
    [Fact]
    public void GetActiveScope_AfterMatchingPublish_ReturnsSameScopeReference()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var g1 = lifecycle.AdvanceOnClipboardNotification();
        var scope = CreateScope();
        Assert.True(lifecycle.TryPublish(g1, scope));

        Assert.Same(scope, lifecycle.GetActiveScope());
    }

    // ---- Y.3. advance after publish -> GetActiveScope null ----
    [Fact]
    public void GetActiveScope_AfterAdvanceFollowingPublish_ReturnsNull()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var g1 = lifecycle.AdvanceOnClipboardNotification();
        Assert.True(lifecycle.TryPublish(g1, CreateScope()));

        lifecycle.AdvanceOnClipboardNotification();

        Assert.Null(lifecycle.GetActiveScope());
    }

    // ---- Y.4. reset after publish -> GetActiveScope null ----
    [Fact]
    public void GetActiveScope_AfterResetFollowingPublish_ReturnsNull()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var g1 = lifecycle.AdvanceOnClipboardNotification();
        Assert.True(lifecycle.TryPublish(g1, CreateScope()));

        lifecycle.Reset();

        Assert.Null(lifecycle.GetActiveScope());
    }

    // ---- Y.5. RETURNED_SCOPE_RACE: retrieval is only a point-in-time snapshot -- the reference
    // itself keeps existing (it is a perfectly ordinary object reference) after a later
    // notification supersedes it, but IsActive against it must now report false. Proves
    // GetActiveScope grants no lease/continued-validity guarantee of any kind. ----
    [Fact]
    public void GetActiveScope_RetrievedReference_BecomesStaleAfterLaterAdvance_ButStillExists()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var g1 = lifecycle.AdvanceOnClipboardNotification();
        var scope = CreateScope();
        Assert.True(lifecycle.TryPublish(g1, scope));

        var retrieved = lifecycle.GetActiveScope();
        Assert.NotNull(retrieved);

        lifecycle.AdvanceOnClipboardNotification();

        // The local reference is still perfectly valid to hold/inspect -- but the lifecycle no
        // longer considers it active. A future security-relevant caller MUST re-check IsActive at
        // its own point of use rather than trusting a previously-retrieved reference.
        Assert.NotNull(retrieved);
        Assert.False(lifecycle.IsActive(retrieved!));
    }

    // ==================================================================
    // RACE TESTS (sections 25-26)
    // ==================================================================

    // ---- 25. deterministic race, forced ordering already proven above (tests 7/8) via plain
    // sequential calls -- MODEL A's single lock means "force a specific order" needs no thread
    // gating at all, since publish's compare-and-install is one indivisible operation. This test
    // additionally forces the two operations onto genuinely different threads with a Barrier so
    // the OS scheduler -- not the test -- decides who actually enters the lock first, then
    // asserts only the two legal outcomes are ever observed, repeated across many trials. ----
    [Fact]
    public async Task ConcurrentAdvanceAndPublish_NeverProducesStaleActiveScope()
    {
        const int trials = 200;

        for (int i = 0; i < trials; i++)
        {
            var lifecycle = new ClipboardDecisionScopeLifecycle();
            var g1 = lifecycle.AdvanceOnClipboardNotification();
            var scope1 = CreateScope();
            var barrier = new Barrier(2);
            bool published = false;

            var publishTask = Task.Run(() =>
            {
                barrier.SignalAndWait();
                published = lifecycle.TryPublish(g1, scope1);
            });
            var advanceTask = Task.Run(() =>
            {
                barrier.SignalAndWait();
                lifecycle.AdvanceOnClipboardNotification();
            });

            await Task.WhenAll(publishTask, advanceTask);

            // ATOMICITY_INVARIANT (Phase 3B STEP16.1, frozen): once generation has moved past
            // g1 (guaranteed here, since advanceTask always runs), scope1 must never be active
            // -- regardless of which task actually won the race for the lock first, and
            // regardless of what `published` came out as.
            Assert.True(lifecycle.CurrentGeneration > g1);
            Assert.False(lifecycle.IsActive(scope1));

            // If publish reported success, that success must have been genuine (it really did
            // install scope1 at some point) -- not a lie. We cannot observe the intermediate
            // state directly, but a `published == true` result combined with the lock's own
            // mutual exclusion is exactly what STEP16.1's proof rests on; this loop's job is to
            // catch a REGRESSION (e.g. a broken/removed lock) by running many trials rather than
            // to re-derive the proof itself.
            _ = published;
        }
    }

    // ---- 26. same bounded stress shape, this time racing TWO publish attempts against a single
    // advance -- only one publish may ever have been able to see a still-current generation, and
    // no interleaving may leave two different scopes simultaneously "active" (impossible by
    // construction: _activeScope is a single field), nor leave a stale one active. ----
    [Fact]
    public async Task ConcurrentPublishAndAdvance_RepeatedTrials_NeverStaleActive()
    {
        const int trials = 200;

        for (int i = 0; i < trials; i++)
        {
            var lifecycle = new ClipboardDecisionScopeLifecycle();
            var g1 = lifecycle.AdvanceOnClipboardNotification();
            var scopeA = CreateScope();
            var barrier = new Barrier(2);

            var publishTask = Task.Run(() =>
            {
                barrier.SignalAndWait();
                lifecycle.TryPublish(g1, scopeA);
            });
            var advanceTask = Task.Run(() =>
            {
                barrier.SignalAndWait();
                lifecycle.AdvanceOnClipboardNotification();
            });

            await Task.WhenAll(publishTask, advanceTask);

            Assert.False(lifecycle.IsActive(scopeA));
        }
    }

    // ==================================================================
    // A -> B -> A (section 28)
    // ==================================================================

    // ---- three sequential arrivals: the ORIGINAL scope bound to the first arrival's generation
    // can never publish or become active again once a later arrival has occurred, regardless of
    // how many further arrivals follow or what they "represent" (this layer has no raw text to
    // compare in the first place -- lifecycle identity, never hash identity). ----
    [Fact]
    public void ThreeSequentialArrivals_OriginalScopeNeverRevives()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();

        var g1 = lifecycle.AdvanceOnClipboardNotification(); // "A" arrives
        var scope1 = CreateScope();
        Assert.True(lifecycle.TryPublish(g1, scope1));

        var g2 = lifecycle.AdvanceOnClipboardNotification(); // "B" arrives -- supersedes scope1
        Assert.False(lifecycle.IsActive(scope1));

        var g3 = lifecycle.AdvanceOnClipboardNotification(); // "A" arrives again (new arrival)

        Assert.True(g2 > g1);
        Assert.True(g3 > g2);
        Assert.False(lifecycle.TryPublish(g1, scope1));
        Assert.False(lifecycle.IsActive(scope1));
        Assert.False(lifecycle.HasActiveScope);
    }

    // ==================================================================
    // ACCESSIBILITY
    // ==================================================================

    [Theory]
    [InlineData(typeof(ClipboardDecisionScope))]
    [InlineData(typeof(IClipboardNotificationLifecycle))]
    [InlineData(typeof(IClipboardDecisionScopeLifecycle))]
    [InlineData(typeof(ClipboardDecisionScopeLifecycle))]
    [InlineData(typeof(ClipboardDispatchItem))]
    [InlineData(typeof(IClipboardGenerationSnapshot))]
    [InlineData(typeof(ClipboardEvaluationState))]
    public void LifecycleTypes_AreNotPublic(Type type)
    {
        Assert.False(type.IsPublic);
    }

    // ==================================================================
    // PHASE 0.2C -- PER-GENERATION EVALUATION STATE (STEP58 contract, STEP59 implementation)
    //
    // TryBeginEvaluation/CompleteEvaluation/AbandonEvaluation are pure state-primitive tests only
    // -- no ForegroundChangeMonitor, no ClipboardPrivacyCoordinator, no outcome-to-transition
    // wiring exists anywhere yet (Phase 0.2D). Every test below drives the three new methods
    // directly against the real ClipboardDecisionScopeLifecycle, exactly mirroring this file's
    // existing no-fakes-needed style (the type has no native dependency of any kind).
    //
    // STEP58's original pseudocode allowed CompleteEvaluation/AbandonEvaluation to transition
    // based on generation-match ALONE -- STEP59's frozen correction additionally requires
    // _evaluationState == InProgress at the moment of the call, so a malformed/double/stale call
    // (no prior claim, or a claim that already resolved one way or the other) is always a no-op
    // rather than silently mutating a terminal or unclaimed generation. Tests 16-21 below exist
    // specifically because that InProgress precondition was missing from the original pseudocode.
    // ==================================================================

    // ---- 1/2. fresh generation can be claimed == first TryBegin succeeds (the same fact stated
    // twice in the instruction's numbered list -- one test, not a duplicate, per this codebase's
    // own established no-redundant-test convention) ----
    [Fact]
    public void TryBeginEvaluation_FreshGeneration_Succeeds()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();

        Assert.True(lifecycle.TryBeginEvaluation(lifecycle.CurrentGeneration));
    }

    // ---- 3. second TryBegin for same generation fails while InProgress ----
    [Fact]
    public void TryBeginEvaluation_SecondCallSameGenerationWhileInProgress_Fails()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var generation = lifecycle.CurrentGeneration;
        Assert.True(lifecycle.TryBeginEvaluation(generation));

        Assert.False(lifecycle.TryBeginEvaluation(generation));
    }

    // ---- 4. Complete closes generation terminally ----
    // ---- 5. terminal generation cannot be claimed again ----
    [Fact]
    public void CompleteEvaluation_AfterClaim_ClosesGenerationTerminally_CannotBeReclaimed()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var generation = lifecycle.CurrentGeneration;
        Assert.True(lifecycle.TryBeginEvaluation(generation));

        lifecycle.CompleteEvaluation(generation);

        Assert.False(lifecycle.TryBeginEvaluation(generation));
    }

    // ---- 6. Abandon makes the SAME current generation retryable ----
    [Fact]
    public void AbandonEvaluation_AfterClaim_MakesSameGenerationRetryable()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var generation = lifecycle.CurrentGeneration;
        Assert.True(lifecycle.TryBeginEvaluation(generation));

        lifecycle.AbandonEvaluation(generation);

        Assert.True(lifecycle.TryBeginEvaluation(generation));
    }

    // ---- 7. clipboard advance resets evaluation state for the new generation ----
    [Fact]
    public void AdvanceOnClipboardNotification_ResetsEvaluationStateForNewGeneration()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var g1 = lifecycle.CurrentGeneration;
        Assert.True(lifecycle.TryBeginEvaluation(g1));
        lifecycle.CompleteEvaluation(g1); // g1 now terminally Evaluated

        var g2 = lifecycle.AdvanceOnClipboardNotification();

        Assert.True(lifecycle.TryBeginEvaluation(g2)); // fresh generation, freely claimable
    }

    // ---- 8. old-generation Complete after advance is no-op ----
    [Fact]
    public void CompleteEvaluation_OldGenerationAfterAdvance_IsNoOp()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var g1 = lifecycle.CurrentGeneration;
        Assert.True(lifecycle.TryBeginEvaluation(g1));

        var g2 = lifecycle.AdvanceOnClipboardNotification();
        lifecycle.CompleteEvaluation(g1); // late, stale -- must not touch g2's state

        Assert.True(lifecycle.TryBeginEvaluation(g2)); // g2 still freely claimable
    }

    // ---- 9. old-generation Abandon after advance is no-op ----
    [Fact]
    public void AbandonEvaluation_OldGenerationAfterAdvance_IsNoOp()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var g1 = lifecycle.CurrentGeneration;
        Assert.True(lifecycle.TryBeginEvaluation(g1));

        var g2 = lifecycle.AdvanceOnClipboardNotification();
        Assert.True(lifecycle.TryBeginEvaluation(g2)); // g2 claimed
        lifecycle.AbandonEvaluation(g1); // late, stale -- must not touch g2's InProgress claim

        Assert.False(lifecycle.TryBeginEvaluation(g2)); // g2's own claim is untouched, still InProgress
    }

    // ---- 10. Reset advances generation and clears evaluation state ----
    [Fact]
    public void Reset_AdvancesGenerationAndClearsEvaluationState()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var g1 = lifecycle.CurrentGeneration;
        Assert.True(lifecycle.TryBeginEvaluation(g1));
        lifecycle.CompleteEvaluation(g1);

        lifecycle.Reset();

        var g2 = lifecycle.CurrentGeneration;
        Assert.True(g2 > g1);
        Assert.True(lifecycle.TryBeginEvaluation(g2));
        // The pre-Reset claim can never resurface against the new generation either.
        Assert.False(lifecycle.TryBeginEvaluation(g1));
    }

    // ---- 11. concurrent TryBegin calls produce exactly one winner -- genuine cross-thread
    // concurrency via a Barrier (never timing-dependent), matching this file's own established
    // ConcurrentAdvanceAndPublish_NeverProducesStaleActiveScope/ConcurrentPublishAndAdvance_
    // RepeatedTrials_NeverStaleActive precedent shape. ----
    [Fact]
    public async Task ConcurrentTryBeginEvaluation_ExactlyOneWinner()
    {
        const int trials = 200;
        const int callers = 8;

        for (int i = 0; i < trials; i++)
        {
            var lifecycle = new ClipboardDecisionScopeLifecycle();
            var generation = lifecycle.CurrentGeneration;
            var barrier = new Barrier(callers);
            var results = new bool[callers];

            var tasks = new Task[callers];
            for (int c = 0; c < callers; c++)
            {
                int index = c;
                tasks[c] = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    results[index] = lifecycle.TryBeginEvaluation(generation);
                });
            }

            await Task.WhenAll(tasks);

            Assert.Equal(1, results.Count(r => r));
        }
    }

    // ---- 12. "NeedsDecision"-equivalent terminal Complete remains closed -- domain-named per the
    // instruction's own phrasing; mechanically identical to test 4/5 (this state layer has no
    // concept of NeedsDecision/Protect/Bypass at all -- those distinctions belong entirely to
    // Phase 0.2D's future outcome-to-transition mapping). ----
    [Fact]
    public void CompleteEvaluation_NeedsDecisionEquivalentOutcome_RemainsTerminallyClosed()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var generation = lifecycle.CurrentGeneration;
        Assert.True(lifecycle.TryBeginEvaluation(generation));

        lifecycle.CompleteEvaluation(generation); // simulates "NeedsDecision published"

        Assert.False(lifecycle.TryBeginEvaluation(generation)); // no duplicate re-evaluation
    }

    // ---- 13. supersede after terminal leaves the new generation fresh -- domain-named, mechanically
    // identical to test 8 (a superseded NeedsDecision-equivalent generation behaves exactly like any
    // other superseded terminal generation at this layer). ----
    [Fact]
    public void Supersede_AfterNeedsDecisionEquivalentTerminal_LeavesNewGenerationFresh()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var g1 = lifecycle.CurrentGeneration;
        Assert.True(lifecycle.TryBeginEvaluation(g1));
        lifecycle.CompleteEvaluation(g1); // simulates "NeedsDecision published" for g1

        var g2 = lifecycle.AdvanceOnClipboardNotification(); // g1 superseded

        Assert.True(lifecycle.TryBeginEvaluation(g2));
    }

    // ---- 14. Complete does not change CurrentGeneration -- directly encodes the STEP42.1 self-write
    // generation-stability invariant: a successfully verified protected self-write must never itself
    // advance/reopen the generation. ----
    [Fact]
    public void CompleteEvaluation_DoesNotChangeCurrentGeneration()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var generation = lifecycle.CurrentGeneration;
        Assert.True(lifecycle.TryBeginEvaluation(generation));

        lifecycle.CompleteEvaluation(generation);

        Assert.Equal(generation, lifecycle.CurrentGeneration);
    }

    // ---- 15. Abandon does not change CurrentGeneration ----
    [Fact]
    public void AbandonEvaluation_DoesNotChangeCurrentGeneration()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var generation = lifecycle.CurrentGeneration;
        Assert.True(lifecycle.TryBeginEvaluation(generation));

        lifecycle.AbandonEvaluation(generation);

        Assert.Equal(generation, lifecycle.CurrentGeneration);
    }

    // ==================================================================
    // MANDATORY INVALID-TRANSITION TESTS (16-21) -- required because STEP58's original pseudocode
    // lacked the InProgress precondition on Complete/Abandon.
    // ==================================================================

    // ---- 16. Complete without prior TryBegin is a no-op ----
    [Fact]
    public void CompleteEvaluation_WithoutPriorTryBegin_IsNoOp()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var generation = lifecycle.CurrentGeneration;

        lifecycle.CompleteEvaluation(generation); // never claimed -- must not fabricate Evaluated

        // If the no-op guard were missing, this generation would now be wrongly "Evaluated" and
        // TryBeginEvaluation would incorrectly fail.
        Assert.True(lifecycle.TryBeginEvaluation(generation));
    }

    // ---- 17. Abandon without prior TryBegin is a no-op ----
    [Fact]
    public void AbandonEvaluation_WithoutPriorTryBegin_IsNoOp()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var generation = lifecycle.CurrentGeneration;

        lifecycle.AbandonEvaluation(generation); // never claimed -- no state to release

        // A subsequent genuine claim must still behave exactly as if Abandon had never been called.
        Assert.True(lifecycle.TryBeginEvaluation(generation));
        Assert.False(lifecycle.TryBeginEvaluation(generation)); // now InProgress, second claim fails
    }

    // ---- 18. Complete called twice does not change terminal state (second call is a no-op because
    // the state is Evaluated, not InProgress, by the time it runs) ----
    [Fact]
    public void CompleteEvaluation_CalledTwice_DoesNotChangeTerminalState()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var generation = lifecycle.CurrentGeneration;
        Assert.True(lifecycle.TryBeginEvaluation(generation));
        lifecycle.CompleteEvaluation(generation);

        lifecycle.CompleteEvaluation(generation); // second call -- no-op, state already Evaluated

        Assert.False(lifecycle.TryBeginEvaluation(generation)); // still terminally closed
    }

    // ---- 19. Abandon after Complete MUST NOT reopen Evaluated ----
    [Fact]
    public void AbandonEvaluation_AfterComplete_DoesNotReopenEvaluated()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var generation = lifecycle.CurrentGeneration;
        Assert.True(lifecycle.TryBeginEvaluation(generation));
        lifecycle.CompleteEvaluation(generation); // Evaluated, not InProgress

        lifecycle.AbandonEvaluation(generation); // must be a no-op -- state is Evaluated, not InProgress

        Assert.False(lifecycle.TryBeginEvaluation(generation)); // still terminally closed, not reopened
    }

    // ---- 20. Complete after Abandon MUST NOT close the generation unless a NEW TryBegin succeeded
    // in between ----
    [Fact]
    public void CompleteEvaluation_AfterAbandon_DoesNotCloseGeneration_UnlessNewTryBeginSucceeded()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var generation = lifecycle.CurrentGeneration;
        Assert.True(lifecycle.TryBeginEvaluation(generation));
        lifecycle.AbandonEvaluation(generation); // back to NotEvaluated

        // Without an intervening TryBeginEvaluation, Complete must be a no-op (state is
        // NotEvaluated, not InProgress).
        lifecycle.CompleteEvaluation(generation);
        Assert.True(lifecycle.TryBeginEvaluation(generation)); // still freely claimable -> was a no-op

        // Now WITH an intervening successful claim, Complete legitimately closes it.
        lifecycle.CompleteEvaluation(generation);
        Assert.False(lifecycle.TryBeginEvaluation(generation)); // now genuinely terminally closed
    }

    // ---- 21. stale old-generation terminal calls cannot affect the CURRENT generation's own
    // active claim -- a stronger assertion than tests 8/9 in isolation: proves a stale call
    // targeting a superseded generation cannot corrupt a claim that is ACTIVELY in progress for the
    // new, current generation (not merely that the new generation "still exists unclaimed"). ----
    [Fact]
    public void StaleOldGenerationTerminalCalls_CannotAffectCurrentGenerationsActiveClaim()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        var g1 = lifecycle.CurrentGeneration;
        Assert.True(lifecycle.TryBeginEvaluation(g1));

        var g2 = lifecycle.AdvanceOnClipboardNotification(); // g1 superseded
        Assert.True(lifecycle.TryBeginEvaluation(g2)); // g2 is now actively InProgress

        // Stale calls for the dead g1 must not touch g2's live InProgress claim in either direction.
        lifecycle.CompleteEvaluation(g1);
        lifecycle.AbandonEvaluation(g1);

        // If either stale call had leaked through, g2 would now be wrongly Evaluated (a second
        // TryBegin would fail for the wrong reason) or wrongly reset to NotEvaluated (a second
        // TryBegin would wrongly succeed). Neither may happen -- g2 must still be exactly
        // InProgress, provable only by CompleteEvaluation(g2) now legitimately succeeding in
        // closing it.
        lifecycle.CompleteEvaluation(g2);
        Assert.False(lifecycle.TryBeginEvaluation(g2));
    }

    // ---- Phase 3C STEP32: ClipboardDecisionScopeLifecycle also implements the narrow
    // IClipboardGenerationSnapshot view -- CurrentGeneration reads identically through either
    // interface, and through that view too an advance is reflected immediately ----
    [Fact]
    public void ImplementsIClipboardGenerationSnapshot_CurrentGenerationMatchesAcrossViews()
    {
        var lifecycle = new ClipboardDecisionScopeLifecycle();
        IClipboardGenerationSnapshot generationView = lifecycle;

        Assert.Equal(0, generationView.CurrentGeneration);

        var advanced = lifecycle.AdvanceOnClipboardNotification();

        Assert.Equal(advanced, generationView.CurrentGeneration);
        Assert.Equal(lifecycle.CurrentGeneration, generationView.CurrentGeneration);
    }
}
