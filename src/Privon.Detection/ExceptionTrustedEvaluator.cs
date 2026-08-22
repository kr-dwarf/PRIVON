using Privon.Core;

namespace Privon.Detection;

/// <summary>
/// Phase 2R.4 -- the "Exception/TrustedPublic evaluation" stage of the audit contract's
/// canonical pipeline (contract §7), placed after ContextRiskAdjustment and before Alias
/// assignment. Consumes only Detection-owned typed values
/// (<see cref="AmbiguousExceptionValue"/>/<see cref="TrustedPublicValue"/>) that the caller has
/// already loaded and mapped from Storage -- this class has no Storage dependency and does not
/// know about <c>ExceptionEntry</c>/<c>TrustedPublicInfoEntry</c>/<c>PiiTypeIdCodec</c> at all
/// (that bridge is a later, separate phase's responsibility).
///
/// Level policy (design doc decisions 62/68-79, restated as code):
///   - Level1 candidates are checked ONLY against <paramref name="exceptions"/>.
///   - Level2 candidates are checked ONLY against <paramref name="trustedPublic"/>.
///   - Level3 candidates are NEVER checked against either list -- always
///     <see cref="TrustState.Untrusted"/>, even if an entry that would otherwise match sits in
///     one or both lists. A persisted store being wrong/misused must never weaken Level3
///     protection.
/// Matching is exact only: <c>candidate.PiiType == entry.PiiType</c> AND
/// <c>candidate.Canonical == entry.CanonicalValue</c>, both required explicitly (never inferred
/// from just one side, even though <see cref="CanonicalValue"/>'s own structural equality
/// already includes PiiType -- see TYPED_VALUE_PIITYPE_INVARIANT below for why the entry's OWN
/// internal consistency is checked as a separate, prior step). No normalization, no
/// prefix/substring/fuzzy matching is performed here -- canonicalization is entirely
/// Detection/Canonicalizer's job, already done by the time a candidate or a stored value
/// reaches this evaluator.
///
/// TYPED_VALUE_PIITYPE_INVARIANT (security-relevant): neither
/// <see cref="AmbiguousExceptionValue"/> nor <see cref="TrustedPublicValue"/> has constructor
/// validation preventing <c>entry.PiiType != entry.CanonicalValue.PiiType</c>. An entry with
/// that inconsistency is treated as malformed here: it is silently skipped (never thrown for --
/// one bad entry must not abort evaluation of every other candidate) and can never itself
/// produce a Trusted result, regardless of what <c>entry.PiiType</c> alone might suggest.
///
/// Output is additive only (<see cref="EvaluatedCandidate"/>): candidate count, order, and
/// every field of each <see cref="DetectionCandidate"/> (PiiType, RawSpan, RiskLevel,
/// Confidence, CanonicalValue, DetectorName) are preserved exactly. This evaluator only ever
/// decides <see cref="TrustState"/> -- it never adds, drops, or mutates a candidate, and never
/// changes RiskLevel or Confidence. <see cref="TrustState.Unknown"/> is never produced here --
/// this is a pure, total function of its typed inputs with no failure mode of its own; Unknown
/// is reserved for a future runtime layer that can actually fail to determine trust (e.g. a
/// Storage read failure upstream of this call).
/// </summary>
public static class ExceptionTrustedEvaluator
{
    public static IReadOnlyList<EvaluatedCandidate> Evaluate(
        DetectionResult detectionResult,
        IReadOnlyList<AmbiguousExceptionValue> exceptions,
        IReadOnlyList<TrustedPublicValue> trustedPublic)
    {
        ArgumentNullException.ThrowIfNull(detectionResult);
        ArgumentNullException.ThrowIfNull(exceptions);
        ArgumentNullException.ThrowIfNull(trustedPublic);

        var results = new List<EvaluatedCandidate>(detectionResult.Candidates.Count);
        foreach (var candidate in detectionResult.Candidates)
        {
            results.Add(new EvaluatedCandidate(candidate, EvaluateOne(candidate, exceptions, trustedPublic)));
        }

        return results;
    }

    // Evaluation branch is chosen entirely by the candidate's CURRENT RiskLevel (i.e. after
    // whatever ContextRiskAdjustment already did to it) -- never a "original detector risk"
    // recovered or tracked separately. This is exactly what makes the (RiskLevel, TrustState)
    // pair in EvaluatedCandidate's doc comment a safe, lossless stand-in for a reason enum.
    private static TrustState EvaluateOne(
        DetectionCandidate candidate,
        IReadOnlyList<AmbiguousExceptionValue> exceptions,
        IReadOnlyList<TrustedPublicValue> trustedPublic) => candidate.RiskLevel switch
    {
        RiskLevel.Level1 => MatchesAny(candidate, exceptions, e => e.PiiType, e => e.CanonicalValue)
            ? TrustState.Trusted
            : TrustState.Untrusted,
        RiskLevel.Level2 => MatchesAny(candidate, trustedPublic, e => e.PiiType, e => e.CanonicalValue)
            ? TrustState.Trusted
            : TrustState.Untrusted,
        // Level3 (and anything not explicitly Level1/Level2): never evaluated against either
        // list at all -- not even attempted -- so a stray matching entry in either list can
        // never affect a Level3 candidate's outcome.
        _ => TrustState.Untrusted,
    };

    private static bool MatchesAny<TEntry>(
        DetectionCandidate candidate,
        IReadOnlyList<TEntry> entries,
        Func<TEntry, PiiType> entryPiiType,
        Func<TEntry, CanonicalValue> entryCanonical)
    {
        foreach (var entry in entries)
        {
            var outerType = entryPiiType(entry);
            var canonical = entryCanonical(entry);

            // TYPED_VALUE_PIITYPE_INVARIANT, step 1: the entry's own outer PiiType and its
            // CanonicalValue's PiiType must agree before this entry is even eligible to match
            // anything. A mismatched entry is malformed -- skip it, do not throw, never let it
            // grant trust.
            if (outerType != canonical.PiiType) continue;

            // TYPED_VALUE_PIITYPE_INVARIANT, step 2: both checks are written out explicitly --
            // neither side is inferred from the other, even though CanonicalValue's own
            // structural equality already implies the PiiType comparison.
            if (candidate.PiiType == outerType && candidate.Canonical == canonical) return true;
        }

        return false;
    }
}
