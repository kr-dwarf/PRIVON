using Privon.Core;
using Privon.Detection;
using Privon.Storage;
using Privon.Windows;

namespace Privon.App;

/// <summary>
/// Phase 3B STEP4 -- the only production implementation of <see cref="IClipboardPrivacyProcessor"/>,
/// extended in Phase 3B STEP8 with real trust/exception evaluation. Owns one reused
/// <see cref="DetectionPipeline.CreateDefault"/> instance -- <c>DetectionPipeline</c> builds its
/// <c>NormalizedView</c>/<c>DetectionContext</c>/candidate list entirely as locals per
/// <c>Detect()</c> call (confirmed by the Phase 3B STEP3 audit), so it is safe and correct to
/// construct exactly once and reuse across every notification rather than rebuilding it per call.
///
/// STEP12 scope boundary (APP_ALIAS_ASSIGNMENT_BOUNDARY): this type runs
/// <c>DetectionPipeline.Detect(snapshot.Text)</c>, and -- only when candidates were actually
/// detected -- <see cref="ITrustExceptionProvider.Load"/>, the real
/// <see cref="Privon.Detection.ExceptionTrustedEvaluator.Evaluate"/>, the real
/// <see cref="Privon.Detection.CandidatePolicyEvaluator.Evaluate"/> (BASE policy only -- it
/// consumes nothing but each candidate's own (RiskLevel, Confidence, TrustState); no
/// RevisionStamp, no runtime bypass grant, no user decision, no Send intent is read or produced
/// here), and now the real <see cref="Privon.Detection.AliasAssigner.Assign"/> against a
/// <see cref="Privon.Detection.AliasMap"/> constructed FRESH, as a local variable, for this one
/// call only (ALIAS_MAP_LIFETIME_FOR_CLIPBOARD, Phase 3B STEP11 audit, frozen:
/// <c>PER_PROCESS_ATTEMPT</c> -- never a field on this or any other type; see
/// <see cref="ProcessWithAssignments"/>). <see cref="Privon.Detection.AliasReplacer"/>/
/// RevisionTracker/grants/protected write are NOT invoked here -- the real
/// <c>DetectionResult</c>/<c>TrustExceptionSnapshot</c>/
/// <c>IReadOnlyList&lt;EvaluatedCandidate&gt;</c>/<c>IReadOnlyList&lt;CandidatePolicyDecision&gt;</c>/
/// <c>AliasMap</c>/<c>IReadOnlyList&lt;AliasAssignment&gt;</c> (and every
/// <c>DetectionCandidate</c>/<c>CanonicalValue</c>/<c>TrustedPublicValue</c>/
/// <c>AmbiguousExceptionValue</c>/<c>AliasToken</c> any of them carry) stay entirely local to
/// <see cref="ProcessWithAssignments"/> and become unreachable the instant it returns; nothing on
/// this type retains a reference to any of them, to <paramref name="snapshot"/>'s <c>Text</c>, or
/// to any other raw/canonical value across calls -- only reference discard/GC-eligibility is
/// claimed here, never deterministic memory zeroization. The alias assignment list itself is
/// deliberately never returned from the PUBLIC <see cref="Process"/> method or carried forward in
/// a stage object (Phase 3B STEP7 audit's FUTURE_POLICY_HANDOFF finding, Option C) -- Phase 3B
/// STEP14's <c>AliasReplacer</c> call lives in <see cref="Process"/> itself, immediately after
/// calling this method, and only the resulting rewritten string (wrapped in
/// <see cref="ClipboardWritePlan"/>) ever leaves this type.
///
/// BASE_BYPASS_NOT_SEND_AUTHORIZATION / BASE_NEEDSDECISION_UNRESOLVED / BASE_PROTECT_NOT_YET_PROTECTED
/// (Phase 3B STEP9 audit, frozen): the resulting counts describe base policy only.
/// <c>BypassCount</c> never means Send-authorized/Verified/a runtime grant -- only "this
/// candidate needs no masking under base policy" (and has two distinct causes: Trusted, or
/// Level1/Low-confidence non-Trusted -- see <see cref="ClipboardPrivacyProcessingResult"/>'s own
/// doc). <c>NeedsDecisionCount</c> is never silently resolved to Protect or Bypass here.
/// <c>ProtectCount</c> never means an alias was already assigned or the clipboard was already
/// rewritten. No <c>Privon.Core.ProtectionState</c> transition happens in this type.
///
/// ALIAS_ASSIGNMENT_NOT_PROTECTED_TEXT (Phase 3B STEP12, frozen): assigning a
/// <c>Privon.Detection.AliasToken</c> to a <c>Protect</c> candidate does NOT by itself mean the
/// clipboard has been rewritten, the text is safe to paste, or
/// <c>Privon.Core.ProtectionState.Verified</c> has been reached -- see NEEDSDECISION_BLOCKS_REPLACEMENT
/// below for what changed in STEP14. A <c>NeedsDecision</c> candidate's <c>Alias == null</c> means
/// only that runtime decision resolution has not happened yet -- never that raw use was approved,
/// Send was authorized, or the state is Verified.
///
/// NEEDSDECISION_BLOCKS_REPLACEMENT / ALL_BYPASS (Phase 3B STEP13 audit, frozen, implemented in
/// <see cref="Process"/>): the real <c>Privon.Detection.AliasReplacer</c> now runs, but ONLY when
/// <c>Result.NeedsDecisionCount == 0</c> (base policy left nothing unresolved) AND
/// <c>Result.ProtectCount &gt; 0</c> (there is at least one span to actually replace) -- producing
/// a <see cref="ClipboardWritePlan"/> carrying the rewritten string. Any <c>NeedsDecision</c>
/// candidate blocks replacement entirely, even when other candidates in the same attempt are
/// <c>Protect</c> -- there is no partial-replacement text ever produced; instead (Phase 3B
/// STEP19) a <see cref="ClipboardDecisionPlan"/> is produced for the candidates that need one --
/// see APP_DECISION_PLAN_BOUNDARY below. All-<c>Bypass</c> (zero <c>Protect</c>, zero
/// <c>NeedsDecision</c>) produces neither plan -- there is nothing to rewrite and nothing to
/// decide. A non-null <see cref="ClipboardWritePlan"/> still means only "a rewritten string
/// exists, ready for a guarded write" -- not that the clipboard was rewritten, that Send is safe,
/// or that <c>ProtectionState.Verified</c> was reached; see <see cref="ClipboardPrivacyProcessingOutcome"/>'s
/// own doc.
///
/// DETECTION_FAILURE_POLICY (frozen, Phase 3B STEP3 audit): an exception from
/// <c>DetectionPipeline.Detect</c> (itself never swallowing an individual detector's own
/// exception -- see <c>DetectionPipeline</c>'s DETECTOR_FAILURE_POLICY) is never caught here
/// either. A thrown exception must reach the caller as "this attempt did not complete," never be
/// reinterpreted as <c>CandidateCount == 0</c> ("no PII found, safe to proceed") -- those are
/// deliberately distinct, non-equivalent outcomes.
///
/// APP_TRUST_PROVIDER_FAILURE_POLICY (Phase 3B STEP7 audit, implemented here): an unexpected
/// exception from <see cref="ITrustExceptionProvider.Load"/> is likewise never caught or
/// substituted with empty lists here -- <c>PrivonLocalStore</c>'s own Load methods are already
/// total (every KNOWN failure mode already collapses to a safe empty list before reaching the
/// provider), so a thrown exception here signals a genuine unexpected runtime/programming
/// failure, which must never be silently disguised as "no trust/exception data, proceed
/// normally." The only thing that keeps the App worker loop alive across either failure is
/// <see cref="ClipboardPrivacyCoordinator"/>'s own outer per-notification catch; this type adds
/// no redundant catch of its own, and never wraps or rethrows with any text-bearing content.
///
/// REPLACER_FAILURE_POLICY (Phase 3B STEP13 audit, implemented here): an unexpected exception
/// from <c>AliasReplacer.Apply</c> is likewise never caught here -- it propagates exactly like a
/// <c>DetectionPipeline.Detect</c>/<see cref="ITrustExceptionProvider.Load"/> failure already
/// does. No <see cref="ClipboardWritePlan"/> is ever produced from a partially-completed replace,
/// and raw clipboard text is never reinterpreted as safe to write because of this failure -- the
/// same outer coordinator catch is what keeps the worker loop alive.
///
/// APP_DECISION_PLAN_BOUNDARY (Phase 3B STEP19, implemented here): when
/// <c>Result.NeedsDecisionCount &gt; 0</c>, <see cref="Process"/> builds a
/// <see cref="ClipboardDecisionPlan"/> entirely from the REAL local <c>assignments</c> chain
/// <see cref="ProcessWithAssignments"/> already produced -- Detection/
/// <c>CandidatePolicyEvaluator</c> are never re-run, and the policy matrix is never
/// hand-recreated. DECISION_ITEM_IDENTITY (Phase 3B STEP18 audit, frozen): each
/// <see cref="ClipboardDecisionItem"/> carries exactly <c>(CanonicalValue, RiskLevel)</c> --
/// see that type's own doc for why nothing else (TrustState/Confidence/PiiType/RawSpan) is
/// retained. DECISION_PLAN_DUPLICATE_POLICY (frozen): items are deduplicated by exact
/// <c>(CanonicalValue, RiskLevel)</c> structural equality, first-appearance order preserved
/// (<c>assignments</c> is already in raw-span appearance order -- <c>AliasAssigner</c>'s own
/// output-order contract -- so a single first-seen-wins scan suffices; no sort, no
/// re-canonicalization). A genuinely different <c>RiskLevel</c> for the same
/// <c>CanonicalValue</c> is never collapsed -- both items survive as distinct entries
/// (STEP18's DUPLICATE_CANONICAL_POLICY: fail-closed, no information silently lost).
/// </summary>
internal sealed class ClipboardPrivacyProcessor : IClipboardPrivacyProcessor
{
    private readonly DetectionPipeline _pipeline;
    private readonly ITrustExceptionProvider _trustExceptionProvider;
    private readonly IProtectionCategorySettingsProvider _categorySettingsProvider;
    private readonly IUserExceptionProvider _userExceptionProvider;

    public ClipboardPrivacyProcessor(
        ITrustExceptionProvider trustExceptionProvider,
        IProtectionCategorySettingsProvider? categorySettingsProvider = null,
        IUserExceptionProvider? userExceptionProvider = null)
        : this(DetectionPipeline.CreateDefault(), trustExceptionProvider, categorySettingsProvider, userExceptionProvider)
    {
    }

    /// <summary>
    /// Test-only seam: lets a targeted regression supply a <c>DetectionPipeline</c> built from a
    /// deliberately-throwing <c>IDetector</c> to prove DETECTION_FAILURE_POLICY, and/or a fake
    /// <see cref="ITrustExceptionProvider"/> to prove PROVIDER_LOAD_COUNT/
    /// APP_TRUST_PROVIDER_FAILURE_POLICY, without needing a separate delegate-shaped abstraction
    /// over either Detection or the trust/exception bridge. The production (public) constructor
    /// above always creates exactly one real default pipeline -- this overload is never used
    /// outside tests.
    ///
    /// <paramref name="categorySettingsProvider"/> (PRIVON v0.2.1 Gate 3A) is OPTIONAL on both
    /// constructors, defaulting to an internal always-<see cref="ProtectionCategorySettings.AllOn"/>
    /// provider when omitted -- AllOn categories is a structural no-op for
    /// <see cref="CategoryPolicyEvaluator"/> (see its own doc), so every pre-existing call site
    /// across this assembly's tests keeps its exact prior behavior unchanged without needing to
    /// pass a third argument. <see cref="PrivonAppComposition"/> is the only caller that supplies
    /// a real <see cref="ProtectionCategorySettingsProvider"/>.
    ///
    /// <paramref name="userExceptionProvider"/> (PRIVON v0.2.1 Gate 3B) is likewise OPTIONAL,
    /// defaulting to an internal always-empty provider -- an empty exception set is a structural
    /// no-op for <see cref="UserExceptionPolicyEvaluator"/> (see its own doc), preserving every
    /// pre-existing call site's exact prior behavior.
    /// </summary>
    internal ClipboardPrivacyProcessor(
        DetectionPipeline pipeline,
        ITrustExceptionProvider trustExceptionProvider,
        IProtectionCategorySettingsProvider? categorySettingsProvider = null,
        IUserExceptionProvider? userExceptionProvider = null)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(trustExceptionProvider);
        _pipeline = pipeline;
        _trustExceptionProvider = trustExceptionProvider;
        _categorySettingsProvider = categorySettingsProvider ?? AllOnProtectionCategorySettingsProvider.Instance;
        _userExceptionProvider = userExceptionProvider ?? EmptyUserExceptionProvider.Instance;
    }

    // Same "default to the real/no-op production implementation when a test doesn't override it"
    // pattern already used elsewhere in this codebase (e.g. ClipboardChangeMonitor's own
    // `_textNative = textNative ?? new Win32ClipboardTextNative();`) -- a stateless singleton, not
    // a new subsystem. Deliberately private/nested: this is an implementation detail of
    // ClipboardPrivacyProcessor's own backward-compatible constructor defaulting, never meant to
    // be referenced or faked independently (use FakeProtectionCategorySettingsProvider for that).
    private sealed class AllOnProtectionCategorySettingsProvider : IProtectionCategorySettingsProvider
    {
        public static readonly AllOnProtectionCategorySettingsProvider Instance = new();
        public ProtectionCategorySettings Load() => ProtectionCategorySettings.AllOn;
    }

    // Identical rationale/pattern as AllOnProtectionCategorySettingsProvider above, for
    // UserExceptionPolicyEvaluator's own structural no-op input (an empty exception set).
    private sealed class EmptyUserExceptionProvider : IUserExceptionProvider
    {
        public static readonly EmptyUserExceptionProvider Instance = new();
        public IReadOnlyList<UserExceptionValue> Load() => [];
    }

    public ClipboardPrivacyProcessingOutcome Process(ForegroundTargetSnapshot expectedTarget, ClipboardTextSnapshot snapshot)
    {
        var (result, assignments) = ProcessWithAssignments(expectedTarget, snapshot);

        // NEEDSDECISION_BLOCKS_REPLACEMENT (frozen): any NeedsDecision candidate blocks
        // AliasReplacer entirely -- this attempt yields a DecisionPlan for the candidates that
        // need one instead, and never a write plan.
        if (result.NeedsDecisionCount > 0)
            return new ClipboardPrivacyProcessingOutcome(result, WritePlan: null, BuildDecisionPlan(assignments!));

        // ALL_BYPASS (frozen): nothing to protect and nothing to decide -- neither plan.
        if (result.ProtectCount == 0)
            return new ClipboardPrivacyProcessingOutcome(result, WritePlan: null, DecisionPlan: null);

        // REPLACER_FAILURE_POLICY: an AliasReplacer failure is never caught here -- see this
        // type's own class doc.
        var replacementText = AliasReplacer.Apply(snapshot.Text, assignments!);
        return new ClipboardPrivacyProcessingOutcome(result, new ClipboardWritePlan(replacementText), DecisionPlan: null);
    }

    // APP_DECISION_PLAN_BOUNDARY / DECISION_ITEM_IDENTITY / DECISION_PLAN_DUPLICATE_POLICY (Phase
    // 3B STEP19, implemented here per the Phase 3B STEP18 audit's frozen contract) -- see this
    // class's own doc above for the full reasoning.
    private static ClipboardDecisionPlan BuildDecisionPlan(IReadOnlyList<AliasAssignment> assignments)
    {
        var seen = new HashSet<ClipboardDecisionItem>();
        var items = new List<ClipboardDecisionItem>();

        foreach (var assignment in assignments)
        {
            if (assignment.Decision.Disposition != CandidateDisposition.NeedsDecision) continue;

            var candidate = assignment.Decision.Candidate.Candidate;
            var item = new ClipboardDecisionItem(candidate.Canonical, candidate.RiskLevel);
            if (seen.Add(item))
            {
                items.Add(item);
            }
        }

        // NeedsDecisionCount > 0 (the only way this method is called) guarantees at least one
        // NeedsDecision assignment exists in the SAME `assignments` list this count was derived
        // from -- an empty plan here would mean that invariant silently broke somewhere upstream.
        // Fail closed rather than returning a plan that claims "nothing to decide" for an attempt
        // the caller already knows has something.
        if (items.Count == 0)
        {
            throw new InvalidOperationException(
                "BuildDecisionPlan was called for an attempt with NeedsDecisionCount > 0 but produced zero decision items.");
        }

        return new ClipboardDecisionPlan(items);
    }

    /// <summary>
    /// Test-only seam (internal, never called by production code beyond <see cref="Process"/>
    /// itself): the actual chain, additionally returning the alias assignment list so a targeted
    /// App-level regression can prove real <c>AliasAssigner</c> wiring/lifetime (same-canonical
    /// reuse, distinct-value numbering, fresh-per-call reset) without widening
    /// <see cref="ClipboardPrivacyProcessingResult"/>'s intentionally metadata-only public shape.
    /// <c>Assignments</c> is <c>null</c> for the NO_PII fast path (the alias stage was never
    /// reached at all -- a stronger, more honest signal than an empty list, which would leave
    /// "ran with zero results" ambiguous with "never ran"). The <see cref="AliasMap"/> instance
    /// itself is constructed here, used, and discarded -- it is NEVER part of this return value
    /// and never escapes this method in any other way either.
    /// </summary>
    internal (ClipboardPrivacyProcessingResult Result, IReadOnlyList<AliasAssignment>? Assignments) ProcessWithAssignments(
        ForegroundTargetSnapshot expectedTarget, ClipboardTextSnapshot snapshot)
    {
        var detectionResult = _pipeline.Detect(snapshot.Text);

        // NO_PII fast path (Phase 3B STEP7 audit's LOAD_TIMING finding): the trust/exception
        // provider is never touched when nothing was detected -- no DPAPI/AES-GCM/file IO, and
        // no sensitive canonical trust/exception values ever enter memory for an attempt that
        // has nothing to evaluate them against. CandidatePolicyEvaluator/AliasAssigner are
        // likewise never reached -- there is nothing for either to act on.
        if (detectionResult.Candidates.Count == 0)
        {
            var zero = new ClipboardPrivacyProcessingResult(CandidateCount: 0, TrustedCount: 0, ProtectCount: 0, NeedsDecisionCount: 0, BypassCount: 0);
            return (zero, null);
        }

        var trust = _trustExceptionProvider.Load();

        // EVALUATOR_CALL_FLOW (Phase 3B STEP7 audit): ExceptionTrustedEvaluator.Evaluate takes
        // exceptions BEFORE trustedPublic -- the exact opposite of TrustExceptionSnapshot's own
        // property declaration order. Swapping these two arguments would silently swap which
        // list Level1 vs Level2 candidates get checked against -- do not "fix" this call to match
        // the snapshot's property order.
        var evaluated = ExceptionTrustedEvaluator.Evaluate(detectionResult, trust.Exceptions, trust.TrustedPublic);

        int trustedCount = 0;
        foreach (var candidate in evaluated)
        {
            if (candidate.TrustState == TrustState.Trusted) trustedCount++;
        }

        // BASE policy only (see APP_ALIAS_ASSIGNMENT_BOUNDARY doc above) -- CandidatePolicyEvaluator
        // is a pure function of each candidate's own (RiskLevel, Confidence, TrustState); no
        // revision/grant/Send-intent state is read or produced here.
        var baseDecisions = CandidatePolicyEvaluator.Evaluate(evaluated);

        // CATEGORY_POLICY_INSERTION (PRIVON v0.2.1 Gate 3A): runs strictly between base policy and
        // AliasAssigner -- see CategoryPolicyEvaluator's own doc for the full contract (Protect-only
        // transform, NeedsDecision/Bypass untouched, Level3 priority automatic). Loaded once per
        // attempt whenever any candidate exists, matching _trustExceptionProvider's own fresh-per-
        // attempt/no-cache policy -- never loaded on the NO_PII fast path above.
        var categorySettings = _categorySettingsProvider.Load();
        var categoryDecisions = CategoryPolicyEvaluator.Apply(baseDecisions, categorySettings);

        // USER_EXCEPTION_POLICY_INSERTION (PRIVON v0.2.1 Gate 3B): runs strictly after
        // CategoryPolicyEvaluator and before AliasAssigner -- see UserExceptionPolicyEvaluator's
        // own doc for the full contract (Protect-only transform, NeedsDecision/Bypass untouched,
        // Level3 priority automatic, identical shape to CategoryPolicyEvaluator). Loaded once per
        // attempt, same fresh-per-attempt/no-cache policy as every other provider here.
        var userExceptions = _userExceptionProvider.Load();
        var decisions = UserExceptionPolicyEvaluator.Apply(categoryDecisions, userExceptions);

        // Counted from the FINAL (post-category, post-user-exception) decisions, not
        // baseDecisions/categoryDecisions -- these counts drive NEEDSDECISION_BLOCKS_REPLACEMENT/
        // ALL_BYPASS branching in Process() below, so they must reflect what AliasAssigner/
        // AliasReplacer actually do with this attempt, not merely what an earlier stage alone
        // would have produced.
        int protectCount = 0, needsDecisionCount = 0, bypassCount = 0;
        foreach (var decision in decisions)
        {
            switch (decision.Disposition)
            {
                case CandidateDisposition.Protect: protectCount++; break;
                case CandidateDisposition.NeedsDecision: needsDecisionCount++; break;
                case CandidateDisposition.Bypass: bypassCount++; break;
            }
        }

        // ALIAS_MAP_LIFETIME_FOR_CLIPBOARD (Phase 3B STEP11 audit, frozen): PER_PROCESS_ATTEMPT
        // -- a brand-new AliasMap for this call only, never a field anywhere. It retains real
        // canonical PII internally for as long as it is reachable, so it must never outlive this
        // method; it is used here and nowhere else, and this local variable is the map's only
        // reference.
        var aliasMap = new AliasMap();
        var assignments = AliasAssigner.Assign(decisions, aliasMap);

        var result = new ClipboardPrivacyProcessingResult(
            detectionResult.Candidates.Count, trustedCount, protectCount, needsDecisionCount, bypassCount);
        return (result, assignments);
    }
}
