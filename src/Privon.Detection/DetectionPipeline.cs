using Privon.Detection.Detectors;

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

    public DetectionPipeline(IReadOnlyList<IDetector> detectors)
    {
        ArgumentNullException.ThrowIfNull(detectors);
        _detectors = detectors;
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
}
