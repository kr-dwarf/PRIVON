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
/// DECISION_TERMINAL_STATUS (PRIVON 0.4.0): the coordinator reports a pending decision as DecisionPending and then never hears of it again, so
/// the opt-in <see cref="ClipboardProtectionStatusTracker"/> would keep describing a decision that is no longer pending. When this attempt
/// ENDS with a fact the tracker already classifies, it is reported once through the SAME <see cref="IClipboardDiagnosticRecorder"/> event the
/// coordinator uses (<see cref="ClipboardDiagnosticEvent.AttemptTerminal"/>), keyed by <see cref="ClipboardDecisionScope.Generation"/> -- which is the
/// coordinator's own attempt id for the content this scope describes: a verified rewrite is Success, a rejected or unverified write is WriteRejected
/// (an unreliable sequence is WriteSkippedUnreliableSequence) and an unexpected exception is ProcessingException (its type name only). Nothing is
/// reported for a stale, partial, awaiting-confirmation or no-write (BypassOnce) outcome: the status then stays what it was, never Verified. The
/// report is made only while the scope is still the ACTIVE one, a failing recorder is swallowed, and the default recorder does nothing.
///
/// PROMPT_FOREGROUND_CARVE_OUT (PRIVON 0.4.0, policy B, Controller-approved; the ONE exception to FRESH_TARGET below): the decision prompt is
/// non-activating, so a mouse click leaves the supported app in the foreground and needs nothing special. A user who can only operate the prompt by
/// ACTIVATING it (keyboard, screen reader) makes PRIVON's own process the foreground, which the fresh capture would reject as unsupported. The prompt
/// therefore passes a <see cref="DecisionPromptForegroundContext"/> -- built fresh for that one press -- ONLY when the prompt window itself was the
/// foreground window at the press, the foreground recorded when the decision was shown was a SUPPORTED Windows target, and the press has a
/// <see cref="DecisionPromptAuthorityBasis"/>: either the application active just before the prompt's was that original target (it stands in), or a fresh
/// Enter / Space addressed to the button raised the click (a MANUAL decision: it restores no target, claims none, and is not proof that a person pressed
/// the key). The attempt then accepts the context if, additionally, (c) the FRESH capture is PRIVON's own process. Nothing else changes: the fresh
/// capture still happens, a different app in the foreground is still refused, the Windows layer still verifies the foreground mechanically against the
/// fresh capture before every read and write, and the revision, whole-plan and scope checks are unchanged. The composer verification is handed the
/// supported target ONLY for the original-target basis; a manual decision hands it none. A Web target is never carved out, and this is not an approval to
/// send and not a way around any other policy.
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
    private readonly IWebClipboardAuthorizationSource? _webAuthorizationSource;
    private readonly IClipboardDiagnosticRecorder _diagnostics;

    public ClipboardDecisionActionResolver(
        IClipboardOperationGate operationGate,
        IClipboardDecisionScopeLifecycle lifecycle,
        IForegroundTargetCapture targetCapture,
        IClipboardReadTransport readTransport,
        IClipboardWriteTransport writeTransport,
        ClipboardPrivacyProcessor processor,
        IClipboardComposerVerificationHandoff verificationHandoff,
        IWebClipboardAuthorizationSource? webAuthorizationSource = null,
        IClipboardDiagnosticRecorder? diagnostics = null)
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
        // Gate 031F5C -- optional, trailing, PRODUCTION_DEFAULT null: PrivonAppComposition passes
        // nothing, so every Web decision action fails closed to Stale() before Phase E ships a
        // concrete IWebClipboardAuthorizationSource. See ClipboardAuthorizationRouter's own doc.
        _webAuthorizationSource = webAuthorizationSource;
        // DECISION_TERMINAL_STATUS -- optional, trailing, PRODUCTION_DEFAULT a recorder that does nothing: only the explicit protection-status opt-in passes one.
        _diagnostics = diagnostics ?? NullClipboardDiagnosticRecorder.Instance;
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
    public Task<ClipboardDecisionActionResult> ResolveAsync(
        ClipboardDecisionScope scope, ClipboardDecisionItem item, ClipboardDecisionIntent intent) =>
        ResolveAsync(scope, item, intent, promptContext: null);

    public async Task<ClipboardDecisionActionResult> ResolveAsync(
        ClipboardDecisionScope scope, ClipboardDecisionItem item, ClipboardDecisionIntent intent, DecisionPromptForegroundContext? promptContext)
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
            // Gate 031F5C -- this is the resolver's OWN fresh authorization moment: a human decision
            // delay is unbounded, so the coordinator attempt's (if any) Web authorization/freshness
            // verifier is NEVER passed in or reused here -- the router is asked again, fresh.
            var target = _targetCapture.Capture();
            var authorization = await ClipboardAuthorizationRouter.AuthorizeAsync(target, _webAuthorizationSource).ConfigureAwait(false);
            ForegroundTargetSnapshot? handoffTarget = target;   // the target the composer verification is told about (none for a manual decision)
            if (authorization.Kind == ClipboardAuthorizationKind.Outside)
            {
                // PROMPT_FOREGROUND_CARVE_OUT: see this type's class doc. Every condition below must hold; any other outside target stays Stale.
                if (!TryAuthorizeFromOwnPrompt(promptContext, target, out authorization)) return ClipboardDecisionActionResult.Stale();
                handoffTarget = promptContext!.Basis == DecisionPromptAuthorityBasis.PreviousAppWasOriginalTarget ? promptContext.OriginalTarget : null;
            }

            IClipboardAuthorizationFreshness? freshness =
                authorization.TryGetWeb(out _, out var webFreshness) ? webFreshness : null;

            // J. FRESH_GUARDED_READ -- never the original raw text, never reconstructed.
            var readResult = await _readTransport.ReadTextSnapshotAsync(target, freshness).ConfigureAwait(false);
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
                if (!_lifecycle.IsActive(scope)) return ClipboardDecisionActionResult.Stale();

                // DECISION_TERMINAL_STATUS -- every pending item was kept as the original: chosen and issued, NOT consumed, NOT protected.
                ReportTerminal(scope, ClipboardDiagnosticTerminalReason.DecisionKeptOriginal);
                return ClipboardDecisionActionResult.Applied(clipboardMutated: false, scopeCommitted: true);
            }

            // AB. WRITE_ELIGIBILITY
            if (!snapshot.HasReliableSequence)
            {
                ReportTerminal(scope, ClipboardDiagnosticTerminalReason.WriteSkippedUnreliableSequence);
                return ClipboardDecisionActionResult.WriteFailed();
            }

            // AC. FINAL_PRE_WRITE_ACTIVE_CHECK
            if (!_lifecycle.IsActive(scope)) return ClipboardDecisionActionResult.Stale();

            // Y/Z. FRESH_ALIASMAP + FINAL_REPLACEMENT -- brand-new AliasMap for this commit attempt
            // only, never reused from the initial (pre-decision) processing pass.
            var aliasMap = new AliasMap();
            var finalAssignments = AliasAssigner.Assign(overlaidDecisions, aliasMap);
            var replacementText = AliasReplacer.Apply(snapshot.Text, finalAssignments);

            // AD. GUARDED_WRITE -- exactly once, this attempt's own fresh target + fresh sequence.
            var writeResult = await _writeTransport.WriteTextIfSequenceMatchesAsync(
                target, snapshot.SequenceNumber, replacementText, freshness).ConfigureAwait(false);
            clipboardMutated = writeResult.ClipboardMutated;

            // AE. WRITE_RESULT_MAPPING
            if (!ClipboardWriteResultClassifier.IsRewriteVerified(writeResult))
            {
                ReportTerminal(scope, ClipboardDiagnosticTerminalReason.WriteRejected);
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

            // AI. COMPOSER_VERIFICATION_HANDOFF (Phase 3C STEP33 audit, frozen; Gate 031F5C
            // WEB_COMPOSER_EXCLUSION) -- gated on this SAME final linearization check, never a
            // separate/earlier/later one: only inside the branch that is about to report
            // Applied(true, true) is a single Publish attempted, using this attempt's own
            // already-captured target/replacementText and the scope's own immutable published
            // Generation (never a fresh CurrentGeneration read). A Web authorization NEVER publishes
            // (the same B2 exclusion as the coordinator's own Publish site) -- this is the ONLY
            // statement gated on authorization.Kind; scope.CommitResolved above and
            // Applied(true, true) below are identical for both. The returned bool is discarded -- no
            // retry, no rollback of the already-committed scope/grants.
            if (_lifecycle.IsActive(scope))
            {
                if (authorization.Kind != ClipboardAuthorizationKind.WebTarget && handoffTarget is { } verifyAgainst)
                    _ = _verificationHandoff.Publish(verifyAgainst, replacementText, scope.Generation);

                // DECISION_TERMINAL_STATUS -- the rewrite was verified in place and committed. It is a Success only when nothing the user decided on was
                // kept as the original: a kept item is still in the text, so that rewrite is not a protection of the whole.
                ReportTerminal(scope, AnyItemKeptAsOriginal(scope)
                    ? ClipboardDiagnosticTerminalReason.DecisionKeptOriginal
                    : ClipboardDiagnosticTerminalReason.Success);
                return ClipboardDecisionActionResult.Applied(clipboardMutated: true, scopeCommitted: true);
            }

            return ClipboardDecisionActionResult.Stale(clipboardMutated: true);
        }
        catch (Exception ex)
        {
            ReportTerminal(scope, ClipboardDiagnosticTerminalReason.ProcessingException, ex.GetType().Name);
            return ClipboardDecisionActionResult.Failed(clipboardMutated);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// ONE_REVISION_PASS (PRIVON 0.4.0, D3A POL-L1-ONCE-R1/R2) -- the guarded product path that applies a scope's live one-revision passes
    /// to the CURRENT clipboard text. It holds the shared operation gate for the whole attempt and mirrors <see cref="ResolveAsync"/>'s
    /// own validation: the scope must still be the ACTIVE one and already committed; a fresh target is captured and authorized (outside =
    /// no pass); the text is read fresh through the guarded transport; and only then does the processor evaluate it and spend a pass
    /// (<see cref="ClipboardPrivacyProcessor.EvaluateOneRevisionPass"/>). The active check is repeated AFTER the evaluation -- the same
    /// check-act-check linearization <see cref="ResolveAsync"/> uses -- and a scope superseded in the meantime yields
    /// <see cref="ClipboardOneRevisionPassOutcome.Stale"/>, never a pass. Two concurrent calls are serialized by the gate and, beneath it, by the
    /// compare-and-swap consumption: the pass can succeed at most once.
    ///
    /// This is NOT send-blocking and not a transmission guarantee (A-1): nothing in the product observes a send, so nothing calls this on a
    /// send. It states only what the next evaluation of this text, under this revision, concludes. Nothing is written to the clipboard,
    /// nothing is rewritten and nothing is persisted; an unexpected exception is reported as <see cref="ClipboardOneRevisionPassOutcome.Failed"/>
    /// without any content and is never a pass.
    /// </summary>
    public async Task<ClipboardOneRevisionPassResult> EvaluateOneRevisionPassAsync(ClipboardDecisionScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_lifecycle.IsActive(scope)) return ClipboardOneRevisionPassResult.Stale();
            if (!scope.Committed) return ClipboardOneRevisionPassResult.Stale();   // no grant exists before the commit

            var target = _targetCapture.Capture();
            var authorization = await ClipboardAuthorizationRouter.AuthorizeAsync(target, _webAuthorizationSource).ConfigureAwait(false);
            if (authorization.Kind == ClipboardAuthorizationKind.Outside) return ClipboardOneRevisionPassResult.Stale();

            IClipboardAuthorizationFreshness? freshness =
                authorization.TryGetWeb(out _, out var webFreshness) ? webFreshness : null;

            var readResult = await _readTransport.ReadTextSnapshotAsync(target, freshness).ConfigureAwait(false);
            if (readResult.Outcome != ClipboardReadOutcome.Success) return ClipboardOneRevisionPassResult.Stale();
            if (!_lifecycle.IsActive(scope)) return ClipboardOneRevisionPassResult.Stale();

            var evaluation = _processor.EvaluateOneRevisionPass(target, readResult.Snapshot!.Value, scope);

            // The linearization point: a pass consumed on a scope that was superseded meanwhile is withheld, not reported.
            return _lifecycle.IsActive(scope)
                ? ClipboardOneRevisionPassResult.Evaluated(evaluation)
                : ClipboardOneRevisionPassResult.Stale();
        }
        catch
        {
            return ClipboardOneRevisionPassResult.Failed();
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private static bool AnyItemKeptAsOriginal(ClipboardDecisionScope scope) =>
        scope.Items.Any(item => scope.GetChoice(item) == ClipboardRuntimeItemChoice.BypassOnce);

    // DECISION_TERMINAL_STATUS: one terminal fact of this attempt, for the scope's own generation, and only while that scope is still the active one
    // (a superseded scope belongs to older content; the newer attempt owns the status). Never throws: a recorder failure must not change the result.
    private void ReportTerminal(ClipboardDecisionScope scope, ClipboardDiagnosticTerminalReason reason, string? exceptionTypeName = null)
    {
        try
        {
            if (!_lifecycle.IsActive(scope)) return;
            _diagnostics.Record(ClipboardDiagnosticEvent.AttemptTerminal(scope.Generation, reason, exceptionTypeName));
        }
        catch
        {
            // SINK_FAILURE_ISOLATION -- same discipline as ClipboardPrivacyCoordinator.Diagnose.
        }
    }

    // PROMPT_FOREGROUND_CARVE_OUT. True only when the press carried a context (the prompt was the foreground at the press and the press has a basis), the
    // FRESH capture is PRIVON's own process, and the target recorded when the decision was shown is a supported WINDOWS target. Anything else -- including
    // a Web target, an unresolved capture, or a capture of any other process -- is refused exactly as before. The basis only decides what the composer
    // check is told afterwards; it never relaxes any of these conditions.
    private static bool TryAuthorizeFromOwnPrompt(
        DecisionPromptForegroundContext? promptContext, ForegroundTargetSnapshot freshTarget, out ClipboardAuthorization authorization)
    {
        authorization = ClipboardAuthorization.Outside;
        if (promptContext is null) return false;
        if (!freshTarget.IsResolved || freshTarget.ProcessId != (uint)Environment.ProcessId) return false;
        if (TargetGate.Match(promptContext.OriginalTarget) is not { } supported) return false;

        authorization = ClipboardAuthorization.ForWindows(supported);
        return true;
    }

    // N/O/P. WHOLE_PLAN_MATCH: builds the exact set of (CanonicalValue, RiskLevel, DetectorId) identities the
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
                // D3B0-A A.9 §7.2 site 1 -- explicit PRESENT check before constructing a
                // ClipboardDecisionItem, whose own Canonical stays NON-nullable. ABSENT is
                // skipped rather than coerced: fail-closed means no decision item is fabricated
                // for a candidate that carries no canonical value.
                if (candidate.Canonical is { } needsDecisionCanonical)
                    currentNeedsDecisionItems.Add(new ClipboardDecisionItem(needsDecisionCanonical, candidate.RiskLevel, candidate.DetectorId));
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
            // §7.2 site 2 -- same explicit PRESENT check as site 1.
            if (candidate.Canonical is not { } itemCanonical) continue;
            var item = new ClipboardDecisionItem(itemCanonical, candidate.RiskLevel, candidate.DetectorId);
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
