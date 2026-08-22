using System.Reflection;
using Privon.App;
using Privon.Windows;

namespace Privon.App.Tests;

// Phase 3C STEP31/31.1/32 -- ClipboardComposerVerifier regression. All tests use
// FakeClipboardGenerationSnapshot + FakeComposerReadTransport (synthetic, OS-free, no mocking
// framework) plus the REAL ClipboardOperationGate (lightweight, deterministic managed-state
// primitive, matching the same "no fake needed" precedent already established for
// ClipboardDecisionScopeLifecycle/ClipboardOperationGate elsewhere in this project).
public class ClipboardComposerVerifierTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    private static ForegroundTargetSnapshot SampleTarget(uint pid = 4242) =>
        new(IsResolved: true, ProcessId: pid, ProcessName: "ChatGPT");

    private static (ClipboardComposerVerifier Verifier, FakeClipboardGenerationSnapshot Generation, FakeComposerReadTransport ComposerTransport, ClipboardOperationGate OperationGate) Create()
    {
        var generation = new FakeClipboardGenerationSnapshot();
        var composerTransport = new FakeComposerReadTransport();
        var operationGate = new ClipboardOperationGate();
        var verifier = new ClipboardComposerVerifier(generation, composerTransport, operationGate);
        return (verifier, generation, composerTransport, operationGate);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? WaitTimeout);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition was not met within the timeout.");
            await Task.Delay(10);
        }
    }

    // ==================================================================
    // PUBLISH (three-phase sequence, Phase 3C STEP31.1)
    // ==================================================================

    // ---- 1. current generation -> true, and the resulting pending record is real (later Verify
    // succeeds against it) ----
    [Fact]
    public async Task Publish_CurrentGeneration_ReturnsTrue_LaterVerifySucceeds()
    {
        var (verifier, generation, composerTransport, _) = Create();
        var target = SampleTarget();
        generation.Generation = 5;

        var published = verifier.Publish(target, "protected text", 5);
        Assert.True(published);

        composerTransport.NextReadResult = ComposerTextReadResult.Success("protected text");
        var result = await verifier.VerifyAsync();

        Assert.Equal(VerificationOutcome.Verified, result.Outcome);
        Assert.Equal(target, composerTransport.ReceivedExpectedTargets[0]);
    }

    // ---- 2. stale generation at PRECHECK -> false, nothing installed ----
    [Fact]
    public async Task Publish_StaleGenerationAtPrecheck_ReturnsFalse_NoRecordInstalled()
    {
        var (verifier, generation, composerTransport, _) = Create();
        generation.Generation = 5;

        var published = verifier.Publish(SampleTarget(), "protected text", 4);
        Assert.False(published);

        var result = await verifier.VerifyAsync();
        Assert.Equal(VerificationOutcome.NotAttempted, result.Outcome);
        Assert.Empty(composerTransport.CallLog);
    }

    // ---- 3. generation changes between PRECHECK and INSTALL -> POST_INSTALL_VALIDATION rejects,
    // the stale record is not left resident ----
    [Fact]
    public async Task Publish_GenerationChangesBeforeInstall_PostInstallValidationRejects_NoRecordRetained()
    {
        var (verifier, generation, composerTransport, _) = Create();
        // First CurrentGeneration read (PRECHECK) sees 5; every read after that (POST_INSTALL_VALIDATION
        // onward) sees 6 -- simulates a notification landing in the PRECHECK-to-INSTALL window.
        generation.Generation = 5;
        generation.ChangeAfterReads = 1;
        generation.ChangedGeneration = 6;

        var published = verifier.Publish(SampleTarget(), "protected text", 5);
        Assert.False(published);

        var result = await verifier.VerifyAsync();
        Assert.Equal(VerificationOutcome.NotAttempted, result.Outcome);
        Assert.Empty(composerTransport.CallLog);
    }

    // ---- 4. generation changes between INSTALL and POST_INSTALL_VALIDATION -- mechanically the
    // SAME observable code window as test 3 above (Publish reads CurrentGeneration exactly twice,
    // with nothing reading it in between INSTALL itself and the post-install check), but recorded
    // as its own test per the Phase 3C STEP31.1 audit's explicit PUBLICATION_RACE_PROOF B/C
    // sub-cases ----
    [Fact]
    public async Task Publish_GenerationChangesAfterInstall_PostInstallValidationRejects_NoRecordRetained()
    {
        var (verifier, generation, composerTransport, _) = Create();
        generation.Generation = 5;
        generation.ChangeAfterReads = 1;
        generation.ChangedGeneration = 6;

        var published = verifier.Publish(SampleTarget(), "protected text", 5);
        Assert.False(published);

        var result = await verifier.VerifyAsync();
        Assert.Equal(VerificationOutcome.NotAttempted, result.Outcome);
        Assert.Empty(composerTransport.CallLog);
    }

    // ---- 5. a rejected (post-install-stale) Publish does not corrupt the slot for a later,
    // genuinely valid, independent Publish -- STALE_PENDING_REINTRODUCTION_AFTER_NOTIFICATION
    // proof at the memory-lifetime boundary ----
    [Fact]
    public async Task Publish_AfterEarlierRejectedPublish_StillSucceedsIndependently()
    {
        var (verifier, generation, composerTransport, _) = Create();
        generation.Generation = 5;
        generation.ChangeAfterReads = 1;
        generation.ChangedGeneration = 6;
        Assert.False(verifier.Publish(SampleTarget(), "stale attempt text", 5));

        // Reset the fake to a plain, non-changing generation for the second, genuinely valid attempt.
        generation.ChangeAfterReads = int.MaxValue;
        generation.Generation = 6;
        var published = verifier.Publish(SampleTarget(), "fresh protected text", 6);
        Assert.True(published);

        composerTransport.NextReadResult = ComposerTextReadResult.Success("fresh protected text");
        var result = await verifier.VerifyAsync();
        Assert.Equal(VerificationOutcome.Verified, result.Outcome);
    }

    // ---- 6. a new successful Publish replaces the old pending record -- no history, no merge --
    // proven by the SINGLE resulting VerifyAsync evaluating against the SECOND (most recent)
    // Publish's own text, never the first (replaced, not queued behind it). Note: because every
    // VerifyAsync outcome is terminal (including Mismatch -- TERMINAL_DISCARD_POLICY), a second,
    // separate VerifyAsync call after one that already consumed the slot would correctly see
    // NotAttempted regardless of which text it targeted -- that is expected one-shot behavior, not
    // tested again here (already covered by the NotAttempted tests above). ----
    [Fact]
    public async Task Publish_Twice_SecondReplacesFirst_NoHistory()
    {
        var (verifier, generation, composerTransport, _) = Create();
        generation.Generation = 1;
        Assert.True(verifier.Publish(SampleTarget(), "first text", 1));

        generation.Generation = 2;
        Assert.True(verifier.Publish(SampleTarget(), "second text", 2));

        composerTransport.NextReadResult = ComposerTextReadResult.Success("second text");
        var result = await verifier.VerifyAsync();

        Assert.Equal(VerificationOutcome.Verified, result.Outcome);
    }

    // ---- null expectedProtectedText is rejected structurally (defense-in-depth via
    // PendingComposerVerification's own constructor guard) ----
    [Fact]
    public void Publish_NullExpectedProtectedText_Throws()
    {
        var (verifier, generation, _, _) = Create();
        generation.Generation = 1;

        Assert.Throws<ArgumentNullException>(() => verifier.Publish(SampleTarget(), null!, 1));
    }

    // ==================================================================
    // INVALIDATE
    // ==================================================================

    // ---- 7. InvalidatePending clears an existing pending record ----
    [Fact]
    public async Task InvalidatePending_ClearsExistingPending()
    {
        var (verifier, generation, composerTransport, _) = Create();
        generation.Generation = 1;
        Assert.True(verifier.Publish(SampleTarget(), "protected text", 1));

        verifier.InvalidatePending();

        var result = await verifier.VerifyAsync();
        Assert.Equal(VerificationOutcome.NotAttempted, result.Outcome);
        Assert.Empty(composerTransport.CallLog);
    }

    // ---- 8. InvalidatePending with nothing pending is a safe no-op ----
    [Fact]
    public void InvalidatePending_NothingPending_DoesNotThrow()
    {
        var (verifier, _, _, _) = Create();

        var exception = Record.Exception(() => verifier.InvalidatePending());

        Assert.Null(exception);
    }

    // ---- InvalidatePending remains callable (and returns immediately) while the shared operation
    // gate is held externally -- it must never touch that gate ----
    [Fact]
    public async Task InvalidatePending_CallableWhileOperationGateHeld()
    {
        var (verifier, generation, _, operationGate) = Create();
        generation.Generation = 1;
        verifier.Publish(SampleTarget(), "protected text", 1);

        await operationGate.WaitAsync();

        // If InvalidatePending ever awaited/acquired the gate, this call itself would hang.
        var invalidateTask = Task.Run(() => verifier.InvalidatePending());
        await invalidateTask.WaitAsync(WaitTimeout);

        operationGate.Release();
    }

    // ==================================================================
    // VERIFYASYNC -- pending / generation / gate order
    // ==================================================================

    // ---- 9. no pending -> NotAttempted, no composer read ----
    [Fact]
    public async Task VerifyAsync_NoPending_ReturnsNotAttempted_NoComposerRead()
    {
        var (verifier, _, composerTransport, _) = Create();

        var result = await verifier.VerifyAsync();

        Assert.Equal(VerificationOutcome.NotAttempted, result.Outcome);
        Assert.Empty(composerTransport.CallLog);
    }

    // ---- 10. pre-read generation mismatch -> Stale, no composer read at all ----
    [Fact]
    public async Task VerifyAsync_PreReadGenerationMismatch_ReturnsStale_NoComposerRead()
    {
        var (verifier, generation, composerTransport, _) = Create();
        generation.Generation = 1;
        Assert.True(verifier.Publish(SampleTarget(), "protected text", 1));

        generation.Generation = 2; // simulates a notification arriving before VerifyAsync runs

        var result = await verifier.VerifyAsync();

        Assert.Equal(VerificationOutcome.Stale, result.Outcome);
        Assert.Empty(composerTransport.CallLog);
    }

    // ---- 11. post-read generation mismatch -> Stale, even when the composer text would otherwise
    // have matched exactly -- the match is never evaluated once staleness is known ----
    [Fact]
    public async Task VerifyAsync_PostReadGenerationMismatch_ReturnsStale_EvenWithMatchingText()
    {
        var (verifier, generation, composerTransport, _) = Create();
        generation.Generation = 1;
        Assert.True(verifier.Publish(SampleTarget(), "protected text", 1));

        composerTransport.HoldReadsUntilReleased = true;
        var verifyTask = verifier.VerifyAsync();
        await WaitUntilAsync(() => composerTransport.PendingHeldReadCount >= 1);

        // A notification arrives while the composer read is in flight.
        generation.Generation = 2;

        composerTransport.ReleaseNextRead(ComposerTextReadResult.Success("protected text"));
        var result = await verifyTask;

        Assert.Equal(VerificationOutcome.Stale, result.Outcome);
    }

    // ==================================================================
    // Phase 3C STEP37.1 -- SESSION_LOCK_INVALIDATION_LINEARIZATION Case A / Case B (corrected test
    // semantics, superseding STEP37's original, unachievable "in-flight VerifyAsync can never
    // return Verified after wall-clock lock" wording). The post-read CurrentGeneration check is the
    // linearization point: Reset() and this check both go through the SAME
    // ClipboardDecisionScopeLifecycle._gate lock, so they are totally ordered relative to each
    // other -- whichever one's critical section runs first determines which branch below applies.
    // No new production check was added (STEP37.1's own CODE_CHANGE_REQUIRED = Option 1 -- the
    // existing check already IS the correct linearization point).
    // ==================================================================

    // ---- Case A: the post-read generation observation sees the generation AFTER a Reset()-shaped
    // advance (simulated the same way an actual session lock would move it) -> Stale, never
    // Verified. Mechanically the same scenario as test 11 above -- re-stated here under the
    // STEP37.1 Case A/B naming so the linearization contract is explicit and discoverable on its
    // own. ----
    [Fact]
    public async Task VerifyAsync_CaseA_PostReadObservationLinearizesAfterReset_ReturnsStale_NeverVerified()
    {
        var (verifier, generation, composerTransport, _) = Create();
        generation.Generation = 1;
        Assert.True(verifier.Publish(SampleTarget(), "protected text", 1));

        composerTransport.HoldReadsUntilReleased = true;
        var verifyTask = verifier.VerifyAsync();
        await WaitUntilAsync(() => composerTransport.PendingHeldReadCount >= 1);

        // Simulates a session-lock Reset() call's generation advance landing while the composer
        // read is in flight -- Reset()'s own critical section runs (logically) before the post-read
        // CurrentGeneration read that is about to happen.
        generation.Generation = 2;

        composerTransport.ReleaseNextRead(ComposerTextReadResult.Success("protected text"));
        var result = await verifyTask;

        Assert.Equal(VerificationOutcome.Stale, result.Outcome);
    }

    // ---- Case B: the post-read generation observation sees the generation BEFORE any Reset() --
    // Verified is a legitimate, allowed point-in-time result, even though a Reset() immediately
    // afterward (wall-clock) would NOT retroactively invalidate this already-returned value. This is
    // not a bug: ComposerVerificationResult.Verified has always meant point-in-time evidence, never
    // a durable/current safety claim (Phase 3C STEP30/STEP30.1, frozen) -- a caller must never treat
    // it as "still true now." Separately, and independently, that same Reset() DOES invalidate the
    // active scope and clears the pending slot for all FUTURE use -- see
    // SessionLockInvalidationTests.cs for that (separate, already-covered) effect. ----
    [Fact]
    public async Task VerifyAsync_CaseB_PostReadObservationLinearizesBeforeAnyReset_ReturnsVerified_AsPointInTimeEvidenceOnly()
    {
        var (verifier, generation, composerTransport, _) = Create();
        generation.Generation = 1;
        Assert.True(verifier.Publish(SampleTarget(), "protected text", 1));
        composerTransport.NextReadResult = ComposerTextReadResult.Success("protected text");

        // No generation change of any kind occurs before or during this call -- the post-read check
        // linearizes with generation still exactly 1, strictly before any hypothetical Reset().
        var result = await verifier.VerifyAsync();

        Assert.Equal(VerificationOutcome.Verified, result.Outcome);
    }

    // ---- gate released after a normal (non-throwing) attempt ----
    [Fact]
    public async Task VerifyAsync_ReleasesGate_AfterNormalAttempt()
    {
        var (verifier, generation, composerTransport, operationGate) = Create();
        generation.Generation = 1;
        verifier.Publish(SampleTarget(), "protected text", 1);
        composerTransport.NextReadResult = ComposerTextReadResult.Success("protected text");

        await verifier.VerifyAsync();

        await operationGate.WaitAsync().WaitAsync(WaitTimeout);
        operationGate.Release();
    }

    // ==================================================================
    // EXACT MATCH
    // ==================================================================

    private static async Task<VerificationOutcome> PublishAndVerify(
        ClipboardComposerVerifier verifier, FakeClipboardGenerationSnapshot generation, FakeComposerReadTransport composerTransport,
        string expectedText, string composerText)
    {
        generation.Generation = 1;
        Assert.True(verifier.Publish(SampleTarget(), expectedText, 1));
        composerTransport.NextReadResult = ComposerTextReadResult.Success(composerText);
        var result = await verifier.VerifyAsync();
        return result.Outcome;
    }

    // ---- 11. exact match -> Verified ----
    [Fact]
    public async Task VerifyAsync_ExactMatch_ReturnsVerified()
    {
        var (verifier, generation, composerTransport, _) = Create();
        Assert.Equal(VerificationOutcome.Verified, await PublishAndVerify(verifier, generation, composerTransport, "hello world", "hello world"));
    }

    // ---- 12. one-character difference -> Mismatch ----
    [Fact]
    public async Task VerifyAsync_OneCharacterDifference_ReturnsMismatch()
    {
        var (verifier, generation, composerTransport, _) = Create();
        Assert.Equal(VerificationOutcome.Mismatch, await PublishAndVerify(verifier, generation, composerTransport, "hello world", "hello worlD"));
    }

    // ---- 13. prefix extra composer text -> Mismatch (no insertion reconstruction, no substring) ----
    [Fact]
    public async Task VerifyAsync_PrefixExtraComposerText_ReturnsMismatch()
    {
        var (verifier, generation, composerTransport, _) = Create();
        Assert.Equal(VerificationOutcome.Mismatch, await PublishAndVerify(verifier, generation, composerTransport, "[전화번호1]", "hi there [전화번호1]"));
    }

    // ---- 13b. suffix extra composer text -> Mismatch ----
    [Fact]
    public async Task VerifyAsync_SuffixExtraComposerText_ReturnsMismatch()
    {
        var (verifier, generation, composerTransport, _) = Create();
        Assert.Equal(VerificationOutcome.Mismatch, await PublishAndVerify(verifier, generation, composerTransport, "[전화번호1]", "[전화번호1] thanks!"));
    }

    // ---- 14. multiline Korean exact match ----
    [Fact]
    public async Task VerifyAsync_MultilineKorean_ExactMatch_ReturnsVerified()
    {
        var (verifier, generation, composerTransport, _) = Create();
        const string text = "홍길동 [전화번호1]\r\n둘째 줄 [이메일1]";
        Assert.Equal(VerificationOutcome.Verified, await PublishAndVerify(verifier, generation, composerTransport, text, text));
    }

    // ---- 15. emoji exact match ----
    [Fact]
    public async Task VerifyAsync_Emoji_ExactMatch_ReturnsVerified()
    {
        var (verifier, generation, composerTransport, _) = Create();
        const string text = "protected 🙂 [전화번호1] 🎉 done";
        Assert.Equal(VerificationOutcome.Verified, await PublishAndVerify(verifier, generation, composerTransport, text, text));
    }

    // ---- 16. zero-width exact match ----
    [Fact]
    public async Task VerifyAsync_ZeroWidth_ExactMatch_ReturnsVerified()
    {
        var (verifier, generation, composerTransport, _) = Create();
        const string text = "before​after [전화번호1]";
        Assert.Equal(VerificationOutcome.Verified, await PublishAndVerify(verifier, generation, composerTransport, text, text));
    }

    // ---- Success carrying a null Text (a contract violation of ComposerTextReadResult) fails
    // closed via Failed, never silently treated as an empty-string match ----
    [Fact]
    public async Task VerifyAsync_SuccessWithNullText_FailsClosed_NeverVerified()
    {
        var (verifier, generation, composerTransport, _) = Create();
        generation.Generation = 1;
        Assert.True(verifier.Publish(SampleTarget(), "protected text", 1));
        composerTransport.NextReadResult = new ComposerTextReadResult { Outcome = ComposerReadOutcome.Success };

        var result = await verifier.VerifyAsync();

        Assert.Equal(VerificationOutcome.Failed, result.Outcome);
    }

    // ==================================================================
    // OUTCOME MAPPING (Phase 3C STEP31.1, explicit -- no numeric enum cast)
    // ==================================================================

    // NOTRUNNING_MAPPING / INVALID_EXPECTED_TARGET_MAPPING / OTHER_OUTCOME_MAPPINGS (Phase 3C
    // STEP31.1) -- each ComposerReadOutcome is asserted individually against its own frozen
    // VerificationOutcome. VerificationOutcome itself is internal, so it cannot be a [Theory]
    // parameter type on a public test method (CS0051) -- the expected value is looked up from this
    // explicit map inside the test body instead, keeping the InlineData rows still one-per-outcome
    // and still an explicit mapping (never a numeric cast).
    private static readonly Dictionary<ComposerReadOutcome, VerificationOutcome> FrozenOutcomeMap = new()
    {
        [ComposerReadOutcome.NotRunning] = VerificationOutcome.Failed,
        [ComposerReadOutcome.InvalidExpectedTarget] = VerificationOutcome.Failed,
        [ComposerReadOutcome.TargetUnavailable] = VerificationOutcome.TargetUnavailable,
        [ComposerReadOutcome.TargetChanged] = VerificationOutcome.TargetChanged,
        [ComposerReadOutcome.ComposerNotFocused] = VerificationOutcome.ComposerNotFocused,
        [ComposerReadOutcome.TextUnavailable] = VerificationOutcome.TextUnavailable,
        [ComposerReadOutcome.AutomationFailure] = VerificationOutcome.AutomationFailure,
    };

    [Theory]
    [InlineData(ComposerReadOutcome.NotRunning)]
    [InlineData(ComposerReadOutcome.InvalidExpectedTarget)]
    [InlineData(ComposerReadOutcome.TargetUnavailable)]
    [InlineData(ComposerReadOutcome.TargetChanged)]
    [InlineData(ComposerReadOutcome.ComposerNotFocused)]
    [InlineData(ComposerReadOutcome.TextUnavailable)]
    [InlineData(ComposerReadOutcome.AutomationFailure)]
    public async Task ComposerReadOutcome_MapsToExpectedVerificationOutcome(ComposerReadOutcome composerOutcome)
    {
        var (verifier, generation, composerTransport, _) = Create();
        generation.Generation = 1;
        Assert.True(verifier.Publish(SampleTarget(), "protected text", 1));
        composerTransport.NextReadResult = ComposerTextReadResult.Failure(composerOutcome);

        var result = await verifier.VerifyAsync();

        Assert.Equal(FrozenOutcomeMap[composerOutcome], result.Outcome);
    }

    // No outcome other than Success can ever yield Verified.
    [Theory]
    [InlineData(ComposerReadOutcome.NotRunning)]
    [InlineData(ComposerReadOutcome.InvalidExpectedTarget)]
    [InlineData(ComposerReadOutcome.TargetUnavailable)]
    [InlineData(ComposerReadOutcome.TargetChanged)]
    [InlineData(ComposerReadOutcome.ComposerNotFocused)]
    [InlineData(ComposerReadOutcome.TextUnavailable)]
    [InlineData(ComposerReadOutcome.AutomationFailure)]
    public async Task NonSuccessComposerOutcome_NeverYieldsVerified(ComposerReadOutcome composerOutcome)
    {
        var (verifier, generation, composerTransport, _) = Create();
        generation.Generation = 1;
        Assert.True(verifier.Publish(SampleTarget(), "protected text", 1));
        composerTransport.NextReadResult = ComposerTextReadResult.Failure(composerOutcome);

        var result = await verifier.VerifyAsync();

        Assert.NotEqual(VerificationOutcome.Verified, result.Outcome);
    }

    // ==================================================================
    // IDENTITY / CONCURRENCY
    // ==================================================================

    // ---- old VerifyAsync pinned to a superseded record can never act on a newer one, even when
    // the newer record's replacement happens while the old attempt's composer read is held open ----
    [Fact]
    public async Task VerifyAsync_PendingReplacedDuringHeldRead_OldAttemptNeverActsOnNewerRecord()
    {
        var (verifier, generation, composerTransport, _) = Create();
        var target = SampleTarget();
        generation.Generation = 1;
        Assert.True(verifier.Publish(target, "first-protected-text", 1));

        composerTransport.HoldReadsUntilReleased = true;
        var staleVerifyTask = verifier.VerifyAsync();
        await WaitUntilAsync(() => composerTransport.PendingHeldReadCount >= 1);

        // A newer clipboard notification arrived and a new verified write published a genuinely
        // different pending record while the old attempt's read was still in flight.
        generation.Generation = 2;
        Assert.True(verifier.Publish(target, "second-protected-text", 2));

        // Release the OLD attempt's held read with text matching the FIRST record's own expected
        // text exactly -- if VerifyAsync ever re-fetched _pending instead of its pinned reference,
        // this could be misclassified against the second record's identity.
        composerTransport.ReleaseNextRead(ComposerTextReadResult.Success("first-protected-text"));
        var staleResult = await staleVerifyTask;

        Assert.Equal(VerificationOutcome.Stale, staleResult.Outcome);
    }

    // ---- the stale attempt's own terminal identity-checked clear must not remove the newer,
    // still-current pending record that replaced it -- proven by a following, independent
    // VerifyAsync still succeeding against the newer record ----
    [Fact]
    public async Task StaleVerifyTerminalCleanup_DoesNotClearNewerPendingRecord()
    {
        var (verifier, generation, composerTransport, _) = Create();
        var target = SampleTarget();
        generation.Generation = 1;
        Assert.True(verifier.Publish(target, "first-protected-text", 1));

        composerTransport.HoldReadsUntilReleased = true;
        var staleVerifyTask = verifier.VerifyAsync();
        await WaitUntilAsync(() => composerTransport.PendingHeldReadCount >= 1);

        generation.Generation = 2;
        Assert.True(verifier.Publish(target, "second-protected-text", 2));

        composerTransport.ReleaseNextRead(ComposerTextReadResult.Success("first-protected-text"));
        var staleResult = await staleVerifyTask;
        Assert.Equal(VerificationOutcome.Stale, staleResult.Outcome);

        composerTransport.HoldReadsUntilReleased = false;
        composerTransport.NextReadResult = ComposerTextReadResult.Success("second-protected-text");
        var freshResult = await verifier.VerifyAsync();

        Assert.Equal(VerificationOutcome.Verified, freshResult.Outcome);
    }

    // ---- shared operation gate: a second, independent gate holder (standing in for
    // ClipboardPrivacyCoordinator's own in-flight attempt) prevents VerifyAsync from proceeding at
    // all until released ----
    [Fact]
    public async Task VerifyAsync_SerializesWithExternalOperationGateHolder()
    {
        var (verifier, generation, composerTransport, operationGate) = Create();
        generation.Generation = 1;
        verifier.Publish(SampleTarget(), "protected text", 1);
        composerTransport.NextReadResult = ComposerTextReadResult.Success("protected text");

        await operationGate.WaitAsync();

        var verifyTask = verifier.VerifyAsync();
        await Task.Delay(50);
        Assert.False(verifyTask.IsCompleted);
        Assert.Empty(composerTransport.CallLog);

        operationGate.Release();
        var result = await verifyTask.WaitAsync(WaitTimeout);
        Assert.Equal(VerificationOutcome.Verified, result.Outcome);
    }

    // ---- unexpected exception -> Failed, gate released, and only the pinned pending record is
    // cleared (proven by a subsequent, fixed-up VerifyAsync observing NotAttempted, not a
    // still-live orphaned record) ----
    [Fact]
    public async Task VerifyAsync_ComposerTransportThrows_ReturnsFailed_ReleasesGate_ClearsPending()
    {
        var (verifier, generation, composerTransport, operationGate) = Create();
        generation.Generation = 1;
        Assert.True(verifier.Publish(SampleTarget(), "protected text", 1));
        composerTransport.ThrowOnRead = new InvalidOperationException("synthetic composer read failure");

        var result = await verifier.VerifyAsync();
        Assert.Equal(VerificationOutcome.Failed, result.Outcome);

        // Gate released -> immediate re-acquisition succeeds.
        await operationGate.WaitAsync().WaitAsync(WaitTimeout);
        operationGate.Release();

        // Pending cleared -> a later VerifyAsync sees NotAttempted, not a stale orphaned record.
        composerTransport.ThrowOnRead = null;
        composerTransport.NextReadResult = ComposerTextReadResult.Success("protected text");
        var afterResult = await verifier.VerifyAsync();
        Assert.Equal(VerificationOutcome.NotAttempted, afterResult.Outcome);
    }

    // ==================================================================
    // STRUCTURAL / PRIVACY
    // ==================================================================

    [Fact]
    public void ClipboardComposerVerifier_HasNoForegroundTargetCaptureDependency()
    {
        var fields = typeof(ClipboardComposerVerifier)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.DoesNotContain(fields, f => f.FieldType == typeof(IForegroundTargetCapture));
        Assert.DoesNotContain(fields, f => f.Name.Contains("Target", StringComparison.OrdinalIgnoreCase)
            && f.Name.Contains("Capture", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ClipboardComposerVerifier_HasNoStringOrHistoryField()
    {
        var fields = typeof(ClipboardComposerVerifier)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.DoesNotContain(fields, f => f.FieldType == typeof(string));
        Assert.DoesNotContain(fields, f => typeof(System.Collections.IEnumerable).IsAssignableFrom(f.FieldType)
            && f.FieldType != typeof(string));
        // Exactly one field is allowed to carry the pending record, and it is single-valued (not a
        // list/queue/history collection).
        Assert.Single(fields, f => f.FieldType == typeof(PendingComposerVerification));
    }

    [Fact]
    public void ComposerVerificationResult_HasNoTextField()
    {
        var properties = typeof(ComposerVerificationResult).GetProperties();

        Assert.DoesNotContain(properties, p => p.PropertyType == typeof(string));
        Assert.Single(properties, p => p.Name == nameof(ComposerVerificationResult.Outcome));
    }

    [Fact]
    public void PendingComposerVerification_HasExactlyTheFrozenThreeProperties()
    {
        var properties = typeof(PendingComposerVerification).GetProperties();

        Assert.Equal(3, properties.Length);
        Assert.Contains(properties, p => p.Name == nameof(PendingComposerVerification.ExpectedTarget));
        Assert.Contains(properties, p => p.Name == nameof(PendingComposerVerification.ExpectedProtectedText));
        Assert.Contains(properties, p => p.Name == nameof(PendingComposerVerification.ExpectedGeneration));
    }

    [Fact]
    public void ComposerVerificationResult_ToString_ContainsOutcomeOnly()
    {
        var result = new ComposerVerificationResult(VerificationOutcome.Verified);

        Assert.Equal("ComposerVerificationResult { Outcome = Verified }", result.ToString());
    }

    [Fact]
    public void PendingComposerVerification_ToString_NeverExposesExpectedProtectedText()
    {
        const string sentinel = "RAW-COMPOSER-VERIFIER-SENTINEL-518203";
        var pending = new PendingComposerVerification(SampleTarget(), sentinel, expectedGeneration: 42);

        var rendered = pending.ToString();

        Assert.DoesNotContain(sentinel, rendered);
        Assert.Contains("42", rendered);
    }

    [Fact]
    public async Task VerifyAsync_ResultNeverExposesRawSentinel_ThroughToStringOrInterpolation()
    {
        const string sentinel = "RAW-COMPOSER-VERIFIER-SENTINEL-284719";
        var (verifier, generation, composerTransport, _) = Create();
        generation.Generation = 1;
        Assert.True(verifier.Publish(SampleTarget(), sentinel, 1));
        composerTransport.NextReadResult = ComposerTextReadResult.Success(sentinel);

        var result = await verifier.VerifyAsync();

        Assert.Equal(VerificationOutcome.Verified, result.Outcome);
        Assert.DoesNotContain(sentinel, result.ToString());
        Assert.DoesNotContain(sentinel, $"{result}");
    }

    [Theory]
    [InlineData(typeof(ClipboardComposerVerifier))]
    [InlineData(typeof(PendingComposerVerification))]
    [InlineData(typeof(ComposerVerificationResult))]
    [InlineData(typeof(VerificationOutcome))]
    [InlineData(typeof(IClipboardGenerationSnapshot))]
    [InlineData(typeof(IComposerReadTransport))]
    [InlineData(typeof(IClipboardComposerVerificationHandoff))]
    [InlineData(typeof(IClipboardComposerVerificationInvalidation))]
    [InlineData(typeof(ComposerReadTransport))]
    public void VerificationTypes_AreNotPublic(Type type)
    {
        Assert.False(type.IsPublic);
    }

    [Theory]
    [InlineData(typeof(ClipboardComposerVerifier))]
    [InlineData(typeof(PendingComposerVerification))]
    [InlineData(typeof(ComposerVerificationResult))]
    public void VerificationTypes_HaveNoDebuggerAttributes(Type type)
    {
        Assert.False(type.IsDefined(typeof(System.Diagnostics.DebuggerDisplayAttribute), inherit: false));
        Assert.False(type.IsDefined(typeof(System.Diagnostics.DebuggerTypeProxyAttribute), inherit: false));
    }
}
