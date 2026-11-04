using Privon.Detection.Detectors;
using Privon.Detection.Family;
using Privon.Detection.Scope;

namespace Privon.Detection;

/// <summary>
/// Phase 2P -- Detection Pipeline Foundation, extended in Phase 2Q.1 with the minimal
/// ContextRiskAdjustment stage. Wires the detectors that already exist into the first four
/// stages of the audit contract's canonical pipeline (contract §7):
///
///   RawText -> NormalizedView+RawIndexMap -> {registered detectors} -> OverlapResolver
///   -> ContextRiskAdjustment
///
/// Everything after ContextRiskAdjustment (Exception/TrustedPublic evaluation, Alias
/// assignment, raw span replacement, Composer write/read-back) is explicitly out of scope here
/// -- see the Phase 2P / Phase 2Q reports. No new PiiType or detector is added in either phase.
///
/// Detector *execution* order (the list passed to the constructor / returned by
/// <see cref="CreateDefault"/>) is a performance/implementation detail only, never a policy
/// decision -- final candidate priority when spans overlap is entirely OverlapResolver's job
/// (RiskLevel, then Confidence, then span length), which this pipeline never second-guesses or
/// duplicates. See <see cref="Detect"/> for how registration order is kept from leaking into
/// the result even in an exact tie.
/// </summary>
public sealed class DetectionPipeline
{
    private readonly IReadOnlyList<IDetector> _detectors;

    // Non-null ONLY for the explicit opt-in constructor below. Every other constructor and CreateDefault() leave it null,
    // so the legacy Detect path is taken exactly as before.
    private readonly FamilyStage? _familyStage;

    public DetectionPipeline(IReadOnlyList<IDetector> detectors)
    {
        ArgumentNullException.ThrowIfNull(detectors);
        _detectors = detectors;
    }

    /// <summary>
    /// D3B0-A A.9 §16.6 -- the added registry constructor. The existing list constructor above
    /// and <see cref="CreateDefault"/> are retained VERBATIM.
    ///
    /// Detectors are taken in the registry's own rank-ascending order (§16.4). That order is an
    /// execution detail only and is NEVER a resolver tiebreaker: this pipeline still pre-sorts by
    /// the candidates' own data before resolution, so registration order cannot leak into any
    /// result (FORBIDDEN_HIDDEN_TIEBREAKERS, §9.2).
    ///
    /// NO_DETECTOR_ACTIVATION_BY_BRIDGE: constructing a registry activates nothing. A detector
    /// runs only because a caller put a real implementation into it.
    /// </summary>
    public DetectionPipeline(DetectorRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var detectors = new List<IDetector>(registry.All.Count);
        foreach (var registration in registry.All) detectors.Add(registration.Detector);
        _detectors = detectors;
    }

    /// <summary>
    /// EXPLICIT OPT-IN. Runs the registry's detectors, the family arbitration seam and the contract
    /// <see cref="CandidateResolver"/> instead of the legacy <see cref="OverlapResolver"/> path:
    ///
    ///   1. every registered detector that is NOT a member of a family runs;
    ///   2. each family source supplies its attempts, which go through the real
    ///      <see cref="DetectorFamilyArbitrationBoundary"/> -- a family member's own Detect is never run, so its
    ///      output cannot be final before arbitration;
    ///   3. only the arbiters' Emitted candidates are added (FAMILY_REJECTED_NEVER_EMITTED);
    ///   4. <see cref="CandidateResolver"/> resolves the combined set (span validation, value-domain validation,
    ///      exact duplicates, overlaps), then <see cref="ContextRiskAdjuster"/> runs as in the legacy path.
    ///
    /// FAIL CLOSED: an unregistered or not-ready detector (queried at use), an unavailable arbiter, a null arbiter
    /// result and any detector, source or arbiter failure surface as exceptions. A completed scan with nothing to
    /// report is an empty <see cref="DetectionResult"/>; it is never produced for a failed scan.
    ///
    /// Nothing activates by constructing this: the caller supplies every detector, source and provider, and the
    /// default pipeline (<see cref="CreateDefault"/>) is unchanged.
    /// </summary>
    public DetectionPipeline(
        DetectorRegistry registry,
        IDetectorFamilyArbiterProvider familyArbiterProvider,
        IReadOnlyList<IDetectorFamilyAttemptSource> familySources)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(familyArbiterProvider);
        ArgumentNullException.ThrowIfNull(familySources);

        _detectors = [];
        _familyStage = new FamilyStage(registry, familyArbiterProvider, familySources, []);
    }

    /// <summary>
    /// The explicit opt-in constructor plus per-country execution scopes. A country whose scope carries
    /// <see cref="CountrySettingObservation.AuthenticatedOff"/> is stopped: the registered detectors the approved ownership
    /// table assigns to it are not run and not readiness-checked, and a family source whose members it owns is not collected,
    /// arbitrated or validated. Every other observation, and every country with no scope, runs in full. Detectors with no
    /// approved owner (Global Core, legacy Korean) always run. The order of the scopes, detectors and requirements never
    /// changes the result; two scopes for one pack are refused.
    ///
    /// The setting itself (authentication, storage, versioning, migration) is NOT part of this type: the caller passes what a
    /// trusted layer observed. Nothing in the product produces such an observation yet.
    /// </summary>
    public DetectionPipeline(
        DetectorRegistry registry,
        IDetectorFamilyArbiterProvider familyArbiterProvider,
        IReadOnlyList<IDetectorFamilyAttemptSource> familySources,
        IReadOnlyList<CountryPackScope> countryScopes)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(familyArbiterProvider);
        ArgumentNullException.ThrowIfNull(familySources);
        ArgumentNullException.ThrowIfNull(countryScopes);

        _detectors = [];
        _familyStage = new FamilyStage(registry, familyArbiterProvider, familySources, countryScopes);
    }

    /// <summary>
    /// The 9 detectors implemented through Phase 2M/2O, in no policy-significant order (see
    /// class doc). BankAccountNumberDetector's own internal Phone/RRN/CardNumber guard (Phase
    /// 2E) is left exactly as it is -- see BANK_ACCOUNT_DIRECT_GUARD_DEBT in the Phase 2P
    /// report; this pipeline runs every detector, including BankAccountNumberDetector, against
    /// the same shared DetectionContext without altering that guard's own behavior.
    /// </summary>
    public static DetectionPipeline CreateDefault() => new(
    [
        new PhoneDetector(),
        new EmailDetector(),
        new ResidentRegistrationNumberDetector(),
        new CardNumberDetector(),
        new BankAccountNumberDetector(),
        new SecretDetector(),
        new IpAddressDetector(),
        new MacAddressDetector(),
        new GpsCoordinateDetector(),
    ]);

    public DetectionResult Detect(string rawText)
    {
        ArgumentNullException.ThrowIfNull(rawText);

        // Built once and shared across every detector call -- RawText itself is never
        // modified, and no detector gets its own NormalizedView.
        var view = NormalizedView.Build(rawText);
        var context = new DetectionContext(view);

        // Explicit opt-in only (see the registry + family constructor). The legacy path below is untouched.
        if (_familyStage is not null) return _familyStage.Detect(rawText, view, context);

        var collected = new List<DetectionCandidate>();
        foreach (var detector in _detectors)
        {
            // DETECTOR_FAILURE_POLICY (see Phase 2P report): an exception from any detector is
            // never caught or swallowed here. Continuing past it and returning whatever other
            // detectors already found would silently report an incomplete scan as if it were
            // complete -- exactly the "검증할 수 없는 상황에서 '보호됨' 표시 금지" situation the
            // audit contract's fail-closed principle exists to prevent. The caller must observe
            // the thrown exception as "this scan did not complete", never as "this text is
            // clean". No fallback/partial-result behavior is invented here.
            collected.AddRange(detector.Detect(context));
        }

        // Detector *registration* order must never affect the final resolved result.
        // OverlapResolver's own ordering is a stable sort over (RiskLevel, Confidence, span
        // length); two candidates tied on all three would otherwise keep whatever relative
        // order they happened to arrive in here, i.e. detector registration order. Sorting by
        // the candidates' own data first -- never by which detector or how many ran before it
        // -- makes the list OverlapResolver receives identical no matter what order the
        // detectors above ran in.
        collected.Sort(static (a, b) =>
        {
            int byStart = a.Span.Start.CompareTo(b.Span.Start);
            if (byStart != 0) return byStart;
            int byLength = a.Span.Length.CompareTo(b.Span.Length);
            if (byLength != 0) return byLength;
            int byType = a.PiiType.CompareTo(b.PiiType);
            if (byType != 0) return byType;
            return string.CompareOrdinal(a.DetectorName, b.DetectorName);
        });

        var resolved = OverlapResolver.Resolve(collected);

        // Phase 2Q.1: ContextRiskAdjustment runs after OverlapResolver, before Exception/
        // TrustedPublic evaluation (contract §7). Reuses the same NormalizedView built above --
        // no second RawText/NormalizedView is created for this step.
        var adjusted = ContextRiskAdjuster.Adjust(view, resolved);

        return new DetectionResult(adjusted);
    }

    /// <summary>
    /// The opt-in execution stage. Held in a nested class so the legacy members above are not interleaved with it.
    /// </summary>
    private sealed class FamilyStage
    {
        private sealed record Family(
            IDetectorFamilyAttemptSource Source, string Name, IReadOnlyList<DetectorId> Members, CountryPack? Owner);

        private sealed record Scope(
            CountryPack Pack, bool Active, IReadOnlyList<DetectorId> RequiredDetectors, IReadOnlyList<string> RequiredFamilies);

        private readonly DetectorRegistry _registry;
        private readonly IDetectorFamilyArbiterProvider _provider;
        private readonly IReadOnlyList<Family> _families;
        private readonly IReadOnlyList<Scope> _scopes;
        private readonly HashSet<string> _memberIds = new(StringComparer.Ordinal);

        public FamilyStage(
            DetectorRegistry registry, IDetectorFamilyArbiterProvider provider, IReadOnlyList<IDetectorFamilyAttemptSource> sources,
            IReadOnlyList<CountryPackScope> countryScopes)
        {
            _registry = registry;
            _provider = provider;
            _scopes = SnapshotScopes(countryScopes);

            // Names and members are read ONCE and snapshotted, as DetectorRegistry does for ids and ranks.
            var families = new List<Family>(sources.Count);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var source in sources)
            {
                ArgumentNullException.ThrowIfNull(source);

                string name = source.FamilyName;
                if (string.IsNullOrWhiteSpace(name))
                    throw new ArgumentException("A family source must name its family.", nameof(sources));
                if (!names.Add(name))
                    throw new ArgumentException("Two family sources cannot share one family name.", nameof(sources));

                var members = source.MemberDetectorIds?.ToArray()
                    ?? throw new ArgumentException("A family source must list its member detectors.", nameof(sources));
                foreach (var member in members)
                {
                    if (!member.HasValue || !_memberIds.Add(member.Value))
                        throw new ArgumentException("A detector cannot belong to two families, nor appear twice.", nameof(sources));
                }

                families.Add(new Family(source, name, members, OwnerOf(members)));
            }

            _families = families;
        }

        // A family belongs to a country only when EVERY member has the same approved owner; unowned members make it
        // unowned (always active); a mixture is ambiguous and refused rather than guessed.
        private static CountryPack? OwnerOf(IReadOnlyList<DetectorId> members)
        {
            CountryPack? owner = null;
            bool anyUnowned = false;
            foreach (var member in members)
            {
                if (DetectorCountryOwnership.TryGetOwner(member, out var memberOwner))
                {
                    if (owner is not null && owner != memberOwner)
                        throw new ArgumentException("A family cannot straddle two countries.", "sources");
                    owner = memberOwner;
                }
                else
                {
                    anyUnowned = true;
                }
            }

            if (owner is not null && anyUnowned)
                throw new ArgumentException("A family cannot mix owned and unowned member detectors.", "sources");
            return owner;
        }

        private static IReadOnlyList<Scope> SnapshotScopes(IReadOnlyList<CountryPackScope> countryScopes)
        {
            var scopes = new List<Scope>(countryScopes.Count);
            var packs = new HashSet<CountryPack>();
            foreach (var scope in countryScopes)
            {
                ArgumentNullException.ThrowIfNull(scope);
                if (!Enum.IsDefined(scope.Pack))
                    throw new ArgumentOutOfRangeException(nameof(countryScopes), "Undefined country pack.");
                if (!Enum.IsDefined(scope.Setting))
                    throw new ArgumentOutOfRangeException(nameof(countryScopes), "Undefined country setting observation.");
                ArgumentNullException.ThrowIfNull(scope.RequiredDetectorIds);
                ArgumentNullException.ThrowIfNull(scope.RequiredFamilyNames);
                if (!packs.Add(scope.Pack))
                    throw new ArgumentException("Two scopes for one country pack are ambiguous.", nameof(countryScopes));

                var required = scope.RequiredDetectorIds.ToArray();
                foreach (var id in required)
                {
                    if (!DetectorCountryOwnership.TryGetOwner(id, out var owner) || owner != scope.Pack)
                        throw new ArgumentException("A required detector must be owned by the scope country pack.", nameof(countryScopes));
                }

                var families = scope.RequiredFamilyNames.ToArray();
                foreach (var name in families)
                {
                    if (string.IsNullOrWhiteSpace(name))
                        throw new ArgumentException("A required family name cannot be blank.", nameof(countryScopes));
                }

                // FAIL_TOWARD_PROTECTION: AuthenticatedOff is the only value that stops a pack.
                scopes.Add(new Scope(scope.Pack, scope.Setting != CountrySettingObservation.AuthenticatedOff, required, families));
            }

            return scopes;
        }

        private bool IsStoppedPack(CountryPack pack) => _scopes.Any(s => s.Pack == pack && !s.Active);

        private bool IsStopped(DetectorId id) =>
            DetectorCountryOwnership.TryGetOwner(id, out var owner) && IsStoppedPack(owner);

        private bool IsStopped(Family family) => family.Owner is { } owner && IsStoppedPack(owner);

        public DetectionResult Detect(string rawText, NormalizedView view, DetectionContext context)
        {
            // Readiness is queried at use, never cached: every detector that is part of this scan (registered, not stopped by
            // an authenticated-OFF country) and every active country requirement must be registered and Ready, or the scan
            // fails (never a silently smaller scan). A stopped country is not consulted at all.
            var required = new List<DetectorId>(_registry.All.Count + _memberIds.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            void Require(DetectorId id)
            {
                if (seen.Add(id.Value)) required.Add(id);
            }

            foreach (var registration in _registry.All)
            {
                if (!IsStopped(registration.Id)) Require(registration.Id);
            }

            foreach (var family in _families)
            {
                if (IsStopped(family)) continue;
                foreach (var member in family.Members) Require(member);
            }

            foreach (var scope in _scopes)
            {
                if (!scope.Active) continue;
                foreach (var id in scope.RequiredDetectors) Require(id);
            }

            RequiredDetectorPrecondition.Assert(required, _registry);

            // An active country required families must have a source of their own that is going to run.
            foreach (var scope in _scopes)
            {
                if (!scope.Active) continue;
                foreach (var name in scope.RequiredFamilies)
                {
                    if (!_families.Any(f => f.Name == name && !IsStopped(f)))
                        throw new FamilyArbitrationUnavailableException(name);
                }
            }

            var collected = new List<DetectionCandidate>();

            // 1. Every registered detector that is neither a family member nor stopped (registry order is rank ascending: an
            //    execution detail only, never a tiebreaker). An exception from any of them propagates.
            foreach (var registration in _registry.All)
            {
                if (_memberIds.Contains(registration.Id.Value) || IsStopped(registration.Id)) continue;
                collected.AddRange(registration.Detector.Detect(context));
            }

            // 2. Each running family: attempts -> the real boundary. Only the arbiter Emitted list is used.
            foreach (var family in _families)
            {
                if (IsStopped(family)) continue;

                var attempts = family.Source.CollectAttempts(context);
                ArgumentNullException.ThrowIfNull(attempts);

                if (!DetectorFamilyArbitrationBoundary.TryArbitrate(_provider, family.Name, attempts, out var arbitration, out _))
                    throw new FamilyArbitrationUnavailableException(family.Name);

                collected.AddRange(arbitration!.Emitted);
            }

            // 3. The contract resolver, strictly after arbitration (R0 span validation, R0.5 value domains, exact
            //    duplicates, overlaps); it never reads the order of its input.
            var resolved = CandidateResolver.Resolve(new ResolutionInput(collected, rawText.Length));

            // 4. The same post-resolution stage the legacy path applies.
            return new DetectionResult(ContextRiskAdjuster.Adjust(view, resolved.Winners));
        }
    }
}
