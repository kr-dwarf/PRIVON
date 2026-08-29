using Privon.Core;
using Privon.Detection;
using Privon.Windows;

namespace Privon.App;

/// <summary>
/// Phase 3B STEP26 -- the actual internal Runtime Decision action path: takes one user-facing
/// <see cref="ClipboardDecisionIntent"/> request against one <see cref="ClipboardDecisionItem"/> of
/// one <see cref="ClipboardDecisionScope"/>, fully revalidates it against the CURRENT clipboard/
/// target/detection state (Phase 3B STEP22 audit's frozen DECISION_TIME_REVALIDATION contract),
/// applies it to the scope's own narrow choice API (Phase 3B STEP25), and -- only once every item
/// in the scope has a final choice -- completes the runtime overlay/write/commit/grant sequence the
/// Phase 3B STEP24/STEP24.1 audits froze. Deliberately NOT part of
/// <see cref="ClipboardPrivacyCoordinator"/> (which remains clipboard-notification mechanics only
/// and still does not know <see cref="ClipboardDecisionScope"/>/<see cref="IClipboardDecisionScopeLifecycle"/>
/// exist) and NOT part of <see cref="ClipboardDecisionSessionPublisher"/> (which remains
/// publication-only and never resolves a session it just published).
///
/// SHARED_GATE_USAGE (Phase 3B STEP22/STEP23 audits, frozen): every <see cref="ResolveAsync"/>
/// attempt awaits <see cref="IClipboardOperationGate.WaitAsync"/> and releases in a
/// <c>finally</c>, holding it for the ENTIRE attempt (initial validation through the final commit/
/// write, exactly mirroring <see cref="ClipboardPrivacyCoordinator.ProcessNotificationAsync"/>'s
/// own acquisition boundary). The <see cref="IClipboardOperationGate"/> instance passed to this
/// type's constructor MUST be the SAME concrete <see cref="ClipboardOperationGate"/> instance
/// <see cref="PrivonAppComposition.BuildGraph"/> gives <see cref="ClipboardPrivacyCoordinator"/> -- two independent
/// instances would serialize nothing relative to each other (Phase 3B STEP22 audit's
/// SERIALIZATION_PRIMITIVE finding, Phase 3B STEP23's OPERATION_GATE_INSTANCE_IDENTITY doc). This
/// gate is never acquired synchronously (<c>Wait()</c>/<c>.Result</c>/<c>.GetAwaiter().GetResult()</c>
/// do not exist on <see cref="IClipboardOperationGate"/> at all) and this type never creates its
/// own private gate.
///
/// LIFECYCLE_IDENTITY (frozen, same reasoning): the <see cref="IClipboardDecisionScopeLifecycle"/>
/// instance passed here must likewise be the SAME concrete <see cref="ClipboardDecisionScopeLifecycle"/>
/// that published the very <see cref="ClipboardDecisionScope"/> a caller passes to
/// <see cref="ResolveAsync"/> -- otherwise every <c>IsActive</c> check below would be checking
/// against the wrong authority entirely.
///
/// STEP24.1_LINEARIZATION (frozen): scope mutation (<see cref="ClipboardDecisionScope.ApplyIntent"/>/
/// <see cref="ClipboardDecisionScope.CommitResolved"/>) is never performed under the lifecycle's
/// own tiny synchronous lock -- it is always <c>check IsActive(scope) -&gt; mutate -&gt; check
/// IsActive(scope) again</c>, and ONLY the second (post-mutation) check is ever treated as the
/// linearization point that lets a result be reported as successfully applied to the STILL-active
/// scope (MODEL A + scope-owned immutable resolution-state swap, Phase 3B STEP24.1 audit's
/// SELECTED_MODEL). If that scope was superseded between mutation and the post-check, the mutation
/// may still exist on the now-detached scope object -- this is accepted (STALE_OBJECT_MUTATION_POLICY)
/// but is NEVER reported as a successful <see cref="ClipboardDecisionActionOutcome.Applied"/>
/// result; no retry is ever attempted, no stale scope is globally searchable, and no zeroization of
/// any kind is claimed.
///
/// SINGLE_EVALUATION (Phase 3B STEP24 audit's PROCESS_REUSE_BOUNDARY, frozen): exactly ONE real
/// current privacy evaluation is performed per attempt, via <see cref="ClipboardPrivacyProcessor.ProcessWithAssignments"/>
/// (the same internal seam <see cref="ClipboardPrivacyProcessor.Process"/> itself already calls)
/// -- never <c>Process</c> followed by a second, separate re-evaluation. Detection/
/// <c>ExceptionTrustedEvaluator</c>/<c>CandidatePolicyEvaluator</c> logic is never duplicated or
/// hand-recreated here; the resulting <c>IReadOnlyList&lt;AliasAssignment&gt;</c> is reused both to
/// revalidate the whole-scope <see cref="ClipboardDecisionPlan"/>-equivalent identity set (Phase 3B
/// STEP22 audit's WHOLE_PLAN_MATCH finding) and, later, to build the runtime overlay for a final
/// commit -- never re-run a second time for the overlay.
///
/// RUNTIME_OVERLAY (Phase 3B STEP24 audit's frozen model): <see cref="CandidatePolicyEvaluator"/>
/// is never modified or re-implemented. Only a <c>NeedsDecision</c> base disposition is ever
/// overridden -- via a plain record <c>with</c> expression, never a new
/// <see cref="CandidatePolicyDecision"/> constructed from scratch -- to <see cref="CandidateDisposition.Protect"/>/
/// <see cref="CandidateDisposition.Bypass"/> according to that exact item's own FINAL
/// <see cref="ClipboardRuntimeItemChoice"/>; every already-Protect/already-Bypass base decision
/// passes through completely unchanged (Phase 3B STEP24 audit's BASE_POLICY_CANDIDATES_REMAIN
/// finding -- a base Protect candidate still forces a write even when every runtime NeedsDecision
/// choice in the same attempt became BypassOnce).
///
/// FRESH_ALIASMAP (Phase 3B STEP11/STEP24 audits, frozen): a brand-new <see cref="AliasMap"/> is
/// constructed here, used for exactly one <see cref="AliasAssigner.Assign"/> call, and discarded --
/// never a field on this type, never reused across attempts, never the SAME map the initial
/// (pre-decision) <see cref="ClipboardPrivacyProcessor.Process"/> call used.
///
/// GRANT_CREATION (Phase 3B STEP24 audit's SELECTED_GRANT_TIMING_MODEL, frozen): a
/// <see cref="ClipboardBypassGrant"/> is NEVER minted at provisional-choice time -- only
/// <see cref="ClipboardDecisionScope.CommitResolved"/> ever mints one, and only once every item in
/// the scope has a final choice AND (when a write was required) that write has already been
/// independently verified. The <see cref="RevisionStamp"/> passed to <c>CommitResolved</c> is
/// either this attempt's own already-validated <c>currentStamp</c> (all-Bypass, no write occurred)
/// or a freshly-observed post-write stamp (obtained via <see cref="ClipboardDecisionScope.ObserveCurrent"/>
/// against the SAME scope-owned <see cref="RevisionTracker"/> -- never a second, independent
/// tracker) -- never the scope's own <see cref="ClipboardDecisionScope.InitialStamp"/> when a write
/// happened.
///
/// ERROR_BOUNDARY (frozen): this type has its own outer failure boundary, entirely independent of
/// <see cref="ClipboardPrivacyCoordinator.RunWorkerAsync"/>'s own outer catch (which this type
/// never touches or relies on) -- an unexpected exception anywhere in one attempt is never
/// re-interpreted as success, is never logged with any content-bearing text, always releases the
/// operation gate (in a <c>finally</c>), never affects the clipboard notification worker in any
/// way, and is reported as <see cref="ClipboardDecisionActionOutcome.Failed"/> carrying whatever
/// <see cref="ClipboardDecisionActionResult.ClipboardMutated"/> value was already known true at the
/// point of failure (e.g. an unexpected failure discovered strictly after a confirmed external
/// write but before this attempt could finish committing).
///
/// NO_SESSION_REPUBLISH (Phase 3B STEP22 audit, frozen): this type never calls
/// <see cref="IClipboardDecisionSessionPublisher.TryPublish"/>, never constructs a new
/// <see cref="RevisionTracker"/> of its own, and never creates a new <see cref="ClipboardDecisionScope"/>
/// -- it acts ONLY against the exact <see cref="ClipboardDecisionScope"/> reference the caller
/// passed to <see cref="ResolveAsync"/>.
///
/// VERIFIED_BOUNDARY (frozen, re-stated): nothing this type ever returns, mints, or commits means
/// <see cref="Privon.Core.ProtectionState.Verified"/> -- that additionally requires an actual
/// composer paste, composer read-back, and final validation (docs/release-gate.md), none of which
/// this type performs. A "verified rewrite" here means only "the guarded write's own read-back
/// verification succeeded" (see <see cref="ClipboardWriteResultClassifier"/>'s own doc).
///
/// COMPOSER_VERIFICATION_HANDOFF (Phase 3C STEP33 audit, implemented Phase 3C STEP34): exactly one
/// <see cref="IClipboardComposerVerificationHandoff.Publish"/> call, gated on the SAME final
/// <see cref="IClipboardDecisionScopeLifecycle.IsActive"/> check that already decides
/// <see cref="ClipboardDecisionActionOutcome.Applied"/> vs. <see cref="ClipboardDecisionActionOutcome.Stale"/>
/// after <see cref="ClipboardDecisionScope.CommitResolved"/> -- never earlier (before the write,
/// before the commit) and never on any path that does not reach
/// <c>Applied(clipboardMutated: true, scopeCommitted: true)</c> (a physically-mutated-but-superseded
/// attempt -- <c>Stale(clipboardMutated: true)</c> -- never publishes; STALE_OBJECT_MUTATION_POLICY
/// still applies). Uses this exact attempt's own already-captured <c>target</c>/<c>replacementText</c>
/// locals (no fresh capture, no recomputation) and <see cref="ClipboardDecisionScope.Generation"/>
/// (never a fresh <see cref="IClipboardDecisionScopeLifecycle.CurrentGeneration"/> read -- see that
/// property's own doc for the TOCTOU proof). The returned <c>bool</c> is deliberately discarded --
/// no retry, no rollback of the already-committed scope/grants -- exactly mirroring
/// <see cref="ClipboardPrivacyCoordinator"/>'s own identical WRITE_HANDOFF precedent for the same
/// interface. An unexpected exception from <c>Publish</c> is never locally caught -- it falls
/// through to this method's own outer <c>catch</c>, reported as
/// <see cref="ClipboardDecisionActionOutcome.Failed"/> with <c>ClipboardMutated</c> still
/// <see langword="true"/>, exactly like any other unexpected failure discovered after a confirmed
/// external write (ERROR_BOUNDARY, above) -- never re-interpreted as success, never rolling back the
/// scope's already-committed resolution state or minted grants.
///
/// OUT OF SCOPE for this STEP (deliberately not implemented anywhere near this type): Send-intent
/// interception, grant consumption/removal, the actual composer read-back itself (performed later,
/// on user trigger, by <see cref="ClipboardComposerVerifier.VerifyAsync"/> -- this type only
/// publishes the pending identity, never reads the composer), persisted trust/exception Storage
/// mutation, Level3 acknowledgment/second-confirmation UI, any WPF dialog/tray UI, the Windows
/// session-lock observer, and response de-alias restoration.
/// </summary>
internal sealed class ClipboardDecisionActionResolver : IClipboardDecisionResolver
{
    private readonly IClipboardOperationGate _operationGate;
    private readonly IClipboardDecisionScopeLifecycle _lifecycle;
    private readonly IForegroundTargetCapture _targetCapture;
    private readonly IClipboardReadTransport _readTransport;
    private readonly IClipboardWriteTransport _writeTransport;
    private readonly ClipboardPrivacyProcessor _processor;
    private readonly IClipboardComposerVerificationHandoff _verificationHandoff;

    public ClipboardDecisionActionResolver(
        IClipboardOperationGate operationGate,
        IClipboardDecisionScopeLifecycle lifecycle,
        IForegroundTargetCapture targetCapture,
        IClipboardReadTransport readTransport,
        IClipboardWriteTransport writeTransport,
        ClipboardPrivacyProcessor processor,
        IClipboardComposerVerificationHandoff verificationHandoff)
    {
        ArgumentNullException.ThrowIfNull(operationGate);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(targetCapture);
        ArgumentNullException.ThrowIfNull(readTransport);
        ArgumentNullException.ThrowIfNull(writeTransport);
        ArgumentNullException.ThrowIfNull(processor);
        ArgumentNullException.ThrowIfNull(verificationHandoff);
        _operationGate = operationGate;
        _lifecycle = lifecycle;
        _targetCapture = targetCapture;
        _readTransport = readTransport;
        _writeTransport = writeTransport;
        _processor = processor;
        _verificationHandoff = verificationHandoff;
    }

    /// <summary>
    /// Runs exactly one full decision-action attempt for <paramref name="intent"/> against
    /// <paramref name="item"/> within <paramref name="scope"/> -- see this type's own class doc for
    /// the complete frozen contract. Never caches, persists, or republishes anything: every
    /// sensitive value touched during the attempt (the fresh raw text, the fresh
    /// <see cref="Privon.Windows.ForegroundTargetSnapshot"/>, the fresh evaluation artifact, any
    /// replacement text) lives only as a local variable of this one call and becomes unreachable
    /// the instant it returns.
    /// </summary>
    public async Task<ClipboardDecisionActionResult> ResolveAsync(
        ClipboardDecisionScope scope, ClipboardDecisionItem item, ClipboardDecisionIntent intent)
    {
        ArgumentNullException.ThrowIfNull(scope);

        await _operationGate.WaitAsync().ConfigureAwait(false);

        // ERROR_BOUNDARY: tracked across the whole attempt so a failure discovered strictly after
        // a confirmed external write can still honestly report ClipboardMutated=true.
        bool clipboardMutated = false;
        try
        {
            // H. INITIAL_ACTIVE_SCOPE_VALIDATION
            if (!_lifecycle.IsActive(scope)) return ClipboardDecisionActionResult.Stale();
            if (scope.Committed) return ClipboardDecisionActionResult.Stale();
            if (!scope.Items.Contains(item)) return ClipboardDecisionActionResult.Stale();

            // I. FRESH_TARGET -- never the original session target, never reused from anywhere else.
            var target = _targetCapture.Capture();
            if (!TargetGate.IsSupportedTarget(target)) return ClipboardDecisionActionResult.Stale();

            // J. FRESH_GUARDED_READ -- never the original raw text, never reconstructed.
            var readResult = await _readTransport.ReadTextSnapshotAsync(target).ConfigureAwait(false);
            if (readResult.Outcome != ClipboardReadOutcome.Success) return ClipboardDecisionActionResult.Stale();

            // K. POST_READ_ACTIVE_CHECK
            if (!_lifecycle.IsActive(scope)) return ClipboardDecisionActionResult.Stale();

            var snapshot = readResult.Snapshot!.Value;

            // L. REVISION_REVALIDATION -- the SAME scope-owned tracker, never a new one.
            var currentStamp = scope.ObserveCurrent(snapshot.Text);
            if (!currentStamp.Matches(scope.InitialStamp)) return ClipboardDecisionActionResult.Stale();

            // M. SINGLE_EVALUATION_PROOF -- exactly one real current privacy evaluation, reusing
            // the same internal seam ClipboardPrivacyProcessor.Process itself calls.
            var (_, assignments) = _processor.ProcessWithAssignments(target, snapshot);

            // N/O/P. WHOLE_PLAN_MATCH -- exact-set identity against scope.Items; a missing sibling,
            // an unexpected new NeedsDecision item, or a policy/trust change that resolved the
            // whole attempt to a WritePlan/neither instead all collapse to the same mismatch here,
            // and (since item is already known to be a member of scope.Items from step H) this
            // single check also proves item itself still needs a decision -- no separate lookup by
            // PiiType/canonical-string/RiskLevel/index is ever performed.
            if (!TryBuildMatchingWholeScopeAssignments(scope, assignments, out var currentAssignments))
                return ClipboardDecisionActionResult.Stale();

            // Q. POST_EVALUATION_ACTIVE_CHECK
            if (!_lifecycle.IsActive(scope)) return ClipboardDecisionActionResult.Stale();

            // R. INTENT_APPLICATION -- the scope's own narrow API owns the transition table; this
            // type never reimplements Level1/Level3 semantics.
            var choice = scope.ApplyIntent(item, intent);

            // S. CHOICE_LINEARIZATION -- the first STEP24.1 linearization point.
            if (!_lifecycle.IsActive(scope)) return ClipboardDecisionActionResult.Stale();

            // T. AWAITING_SECOND_CONFIRMATION
            if (choice == ClipboardRuntimeItemChoice.AwaitingLevel3Confirmation)
                return ClipboardDecisionActionResult.AwaitingSecondConfirmation();

            // U. SIBLING_UNRESOLVED_BARRIER -- no partial write, no partial commit, no grant.
            if (!scope.AllItemsResolved)
                return ClipboardDecisionActionResult.Applied(clipboardMutated: false, scopeCommitted: false);

            // V/W/X. RUNTIME_OVERLAY -- reuses currentAssignments from the SAME single evaluation
            // above; CandidatePolicyEvaluator itself is never touched.
            var overlaidDecisions = BuildOverlaidDecisions(scope, currentAssignments);
            bool hasProtect = overlaidDecisions.Any(d => d.Disposition == CandidateDisposition.Protect);

            // AA. NO-PROTECT FINAL COMMIT -- no write, grants minted against this attempt's own
            // already-validated currentStamp.
            if (!hasProtect)
            {
                if (!_lifecycle.IsActive(scope)) return ClipboardDecisionActionResult.Stale();
                scope.CommitResolved(currentStamp);
                return _lifecycle.IsActive(scope)
                    ? ClipboardDecisionActionResult.Applied(clipboardMutated: false, scopeCommitted: true)
                    : ClipboardDecisionActionResult.Stale();
            }

            // AB. WRITE_ELIGIBILITY
            if (!snapshot.HasReliableSequence) return ClipboardDecisionActionResult.WriteFailed();

            // AC. FINAL_PRE_WRITE_ACTIVE_CHECK
            if (!_lifecycle.IsActive(scope)) return ClipboardDecisionActionResult.Stale();

            // Y/Z. FRESH_ALIASMAP + FINAL_REPLACEMENT -- brand-new AliasMap for this commit attempt
            // only, never reused from the initial (pre-decision) processing pass.
            var aliasMap = new AliasMap();
            var finalAssignments = AliasAssigner.Assign(overlaidDecisions, aliasMap);
            var replacementText = AliasReplacer.Apply(snapshot.Text, finalAssignments);

            // AD. GUARDED_WRITE -- exactly once, this attempt's own fresh target + fresh sequence.
            var writeResult = await _writeTransport.WriteTextIfSequenceMatchesAsync(
                target, snapshot.SequenceNumber, replacementText).ConfigureAwait(false);
            clipboardMutated = writeResult.ClipboardMutated;

            // AE. WRITE_RESULT_MAPPING
            if (!ClipboardWriteResultClassifier.IsRewriteVerified(writeResult))
            {
                return writeResult.ClipboardMutated
                    ? ClipboardDecisionActionResult.MutatedUnverified()
                    : ClipboardDecisionActionResult.WriteFailed();
            }

            // AG. POST_WRITE_REVISION -- optional fail-fast before touching the scope's tracker again.
            if (!_lifecycle.IsActive(scope)) return ClipboardDecisionActionResult.Stale(clipboardMutated: true);

            var postWriteStamp = scope.ObserveCurrent(replacementText);

            // AH. POST_WRITE_COMMIT -- fail-fast before CommitResolved, then the load-bearing
            // post-mutation linearization check that actually decides Applied vs. Stale.
            if (!_lifecycle.IsActive(scope)) return ClipboardDecisionActionResult.Stale(clipboardMutated: true);

            scope.CommitResolved(postWriteStamp);

            // AI. COMPOSER_VERIFICATION_HANDOFF (Phase 3C STEP33 audit, frozen) -- gated on this
            // SAME final linearization check, never a separate/earlier/later one: only inside the
            // branch that is about to report Applied(true, true) is a single Publish attempted,
            // using this attempt's own already-captured target/replacementText and the scope's own
            // immutable published Generation (never a fresh CurrentGeneration read). The returned
            // bool is discarded -- no retry, no rollback of the already-committed scope/grants.
            if (_lifecycle.IsActive(scope))
            {
                _ = _verificationHandoff.Publish(target, replacementText, scope.Generation);
                return ClipboardDecisionActionResult.Applied(clipboardMutated: true, scopeCommitted: true);
            }

            return ClipboardDecisionActionResult.Stale(clipboardMutated: true);
        }
        catch
        {
            return ClipboardDecisionActionResult.Failed(clipboardMutated);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    // N/O/P. WHOLE_PLAN_MATCH: builds the exact set of (CanonicalValue, RiskLevel) identities the
    // fresh evaluation currently reports as NeedsDecision -- same identity/dedup semantics as
    // ClipboardPrivacyProcessor.BuildDecisionPlan (structural equality, no re-canonicalization) --
    // and requires it to be an EXACT set match against scope.Items (same count, no missing
    // sibling, no unexpected new item). A null assignments list (the fresh evaluation's own
    // NO_PII fast path) is treated as an empty set, which can only ever mismatch a non-empty
    // scope.Items -- exactly the correct "policy/trust change resolved everything" Stale outcome.
    private static bool TryBuildMatchingWholeScopeAssignments(
        ClipboardDecisionScope scope, IReadOnlyList<AliasAssignment>? assignments, out IReadOnlyList<AliasAssignment> matched)
    {
        var currentNeedsDecisionItems = new HashSet<ClipboardDecisionItem>();
        if (assignments is not null)
        {
            foreach (var assignment in assignments)
            {
                if (assignment.Decision.Disposition != CandidateDisposition.NeedsDecision) continue;
                var candidate = assignment.Decision.Candidate.Candidate;
                currentNeedsDecisionItems.Add(new ClipboardDecisionItem(candidate.Canonical, candidate.RiskLevel));
            }
        }

        if (!currentNeedsDecisionItems.SetEquals(scope.Items))
        {
            matched = [];
            return false;
        }

        matched = assignments ?? [];
        return true;
    }

    // W. RUNTIME_OVERLAY: a base decision that is already Protect/Bypass passes through completely
    // unchanged; a NeedsDecision base decision is overridden -- via `with`, never reconstructed
    // from scratch -- to Protect/Bypass according to that exact item's own FINAL scope choice.
    // Every item reached here is guaranteed (by the U barrier that gates this call) to have a
    // final choice -- Unresolved/AwaitingLevel3Confirmation are structurally unreachable, and the
    // fail-closed throw below exists purely as an invariant guard, never a real production path.
    private static List<CandidatePolicyDecision> BuildOverlaidDecisions(
        ClipboardDecisionScope scope, IReadOnlyList<AliasAssignment> assignments)
    {
        var overlaid = new List<CandidatePolicyDecision>(assignments.Count);
        foreach (var assignment in assignments)
        {
            var decision = assignment.Decision;
            if (decision.Disposition != CandidateDisposition.NeedsDecision)
            {
                overlaid.Add(decision);
                continue;
            }

            var candidate = decision.Candidate.Candidate;
            var item = new ClipboardDecisionItem(candidate.Canonical, candidate.RiskLevel);
            var overlaidDisposition = scope.GetChoice(item) switch
            {
                ClipboardRuntimeItemChoice.Protect => CandidateDisposition.Protect,
                ClipboardRuntimeItemChoice.BypassOnce => CandidateDisposition.Bypass,
                _ => throw new InvalidOperationException(
                    "Invariant violation: a NeedsDecision item had no final runtime choice at final-commit time."),
            };
            overlaid.Add(decision with { Disposition = overlaidDisposition });
        }

        return overlaid;
    }
}
