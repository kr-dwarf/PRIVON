using Privon.Core;

namespace Privon.Detection;

/// <summary>
/// Phase 2S.2 -- the "Candidate Policy Decision" stage inserted into the audit contract's
/// canonical pipeline (contract §7) between Exception/TrustedPublic evaluation and Alias
/// assignment (a clarification of §7, not an architecture deviation -- see the Phase 2S.1
/// report). Consumes only <see cref="EvaluatedCandidate"/> values already produced by
/// <see cref="ExceptionTrustedEvaluator"/>; knows nothing about Storage, revisions, or UI
/// timing.
///
/// Policy matrix (design doc decisions 60/68/193/196, audit contract §2.1, confirmed in the
/// Phase 2S / 2S.1 reports):
///
///   Level1, Trusted            -> Bypass (any Confidence)
///   Level1, non-Trusted, High  -> Protect
///   Level1, non-Trusted, Medium -> NeedsDecision
///   Level1, non-Trusted, Low   -> Bypass
///
///   Level2, Trusted            -> Bypass (any Confidence)
///   Level2, non-Trusted        -> Protect (any Confidence -- Confidence never weakens Level2;
///                                 §2.1 states Level2 protection unconditionally)
///
///   Level3, ANY TrustState, ANY Confidence -> NeedsDecision, always
///
/// "non-Trusted" means <see cref="TrustState.Untrusted"/> OR <see cref="TrustState.Unknown"/>
/// -- both fall through to the exact same branch below. Unknown is never special-cased toward
/// either Protect or Bypass; it simply isn't Trusted (see the Phase 2S.1 report's
/// UNKNOWN_SEMANTICS finding).
///
/// Level3 defense-in-depth: RiskLevel is checked FIRST, unconditionally, before TrustState is
/// even inspected. A real <see cref="ExceptionTrustedEvaluator"/> can never itself produce a
/// Level3 candidate with TrustState.Trusted (Phase 2R.4's own Level3 rule), but this evaluator
/// does not rely on that guarantee holding upstream -- even a Level3/Trusted input here still
/// resolves to NeedsDecision, never Bypass.
///
/// Deliberately NOT included here: Alias assignment, revision-bound overrides ("이번만 통과" /
/// "원문 사용"), and <see cref="ProtectionState"/> aggregation. This evaluator is a pure
/// function of a single candidate's own (RiskLevel, Confidence, TrustState) -- it has no
/// concept of "this exact revision was already overridden", and produces exactly one
/// <see cref="CandidateDisposition"/> per input candidate, never fewer or more.
/// </summary>
public static class CandidatePolicyEvaluator
{
    public static IReadOnlyList<CandidatePolicyDecision> Evaluate(IReadOnlyList<EvaluatedCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var results = new List<CandidatePolicyDecision>(candidates.Count);
        foreach (var candidate in candidates)
        {
            results.Add(new CandidatePolicyDecision(candidate, Decide(candidate)));
        }

        return results;
    }

    public static CandidateDisposition Decide(EvaluatedCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        // All three enum inputs are validated up front, regardless of whether the branch a
        // given combination ends up taking would actually consume that specific field (e.g.
        // Level2 never looks at Confidence) -- an invalid/undefined value must never pass
        // through silently just because this particular decision path didn't happen to read
        // it. See the Phase 2S.2 report's INVALID_STATE_HANDLING section.
        ValidateDefined(candidate.Candidate.RiskLevel);
        ValidateDefined(candidate.Candidate.Confidence);
        ValidateDefined(candidate.TrustState);

        // Level3 defense-in-depth: checked first and unconditionally, before TrustState is
        // even inspected -- see class doc.
        if (candidate.Candidate.RiskLevel == RiskLevel.Level3)
        {
            return CandidateDisposition.NeedsDecision;
        }

        if (candidate.TrustState == TrustState.Trusted)
        {
            return CandidateDisposition.Bypass;
        }

        // TrustState.Untrusted and TrustState.Unknown both reach here -- neither is
        // special-cased differently from the other from this point on.
        return candidate.Candidate.RiskLevel switch
        {
            RiskLevel.Level2 => CandidateDisposition.Protect,
            RiskLevel.Level1 => candidate.Candidate.Confidence switch
            {
                DetectionConfidence.High => CandidateDisposition.Protect,
                DetectionConfidence.Medium => CandidateDisposition.NeedsDecision,
                DetectionConfidence.Low => CandidateDisposition.Bypass,
                _ => throw new ArgumentOutOfRangeException(nameof(candidate), candidate.Candidate.Confidence, "Undefined DetectionConfidence value."),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(candidate), candidate.Candidate.RiskLevel, "Undefined RiskLevel value."),
        };
    }

    private static void ValidateDefined<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, $"Undefined {typeof(TEnum).Name} value.");
        }
    }
}
