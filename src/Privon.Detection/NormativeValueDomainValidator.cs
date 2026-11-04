using Privon.Core;

namespace Privon.Detection;

/// <summary>
/// D3B0-A A.9 §11.2 -- R0.5, three checks in FROZEN PRECEDENCE, each evaluated over the ENTIRE
/// input set before the next begins. Candidate-first validation is prohibited (§15).
/// </summary>
internal static class NormativeValueDomainValidator
{
    public static void Validate(IReadOnlyList<DetectionCandidate> candidates)
    {
        // ---- check 1: no normative semantic metadata outside the eleven fields of §8 ----
        // Enumerates Names ONLY and NEVER calls TryGetOpaqueValue. The failure is a function of
        // "at least one candidate has Count > 0", so no ordering can affect it.
        for (int i = 0; i < candidates.Count; i++)
        {
            var bag = candidates[i].AdditionalNormativeFields;
            if (bag is not null && bag.Count > 0)
                throw new UnknownCandidateSemanticFieldException();
        }

        // ---- check 2: every enum-like field is in its frozen closed domain (§13) ----
        for (int i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            if (!FrozenPositions.TryRiskLevel(c.RiskLevel, out _)
                || !FrozenPositions.TryConfidence(c.Confidence, out _)
                || !FrozenPositions.TryEvidence(c.Evidence, out _)
                || !FrozenPositions.TryOwnerClaim(c.OwnerClaim, out _)
                || !IsKnownSemanticType(c.PiiType))
                throw new InvalidNormativeValueDomainException(
                    InvalidNormativeValueDomainException.UnknownEnumValueCode);

            // §4.4 -- ABSENT means NO CanonicalValue type or value-domain check runs at all.
            if (c.Canonical is { } canonical && !IsKnownSemanticType(canonical.PiiType))
                throw new InvalidNormativeValueDomainException(
                    InvalidNormativeValueDomainException.UnknownEnumValueCode);
        }

        // ---- check 3: every normative string field is a ValidUnicodeScalarSequence (§12.3) ----
        // Skips ABSENT optional fields. NO_REPLACEMENT_FALLBACK: an invalid sequence FAILS; no
        // U+FFFD substitution, no sanitisation, no normalization, no candidate drop.
        for (int i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];

            if (c.DetectorId.HasValue && !UnicodeScalarValidator.IsValidScalarSequence(c.DetectorId.Value))
                throw Invalid();

            if (!UnicodeScalarValidator.IsValidScalarSequence(PiiTypeIdCodec.ToStableId(c.PiiType)))
                throw Invalid();

            if (c.Canonical is { } canonical)
            {
                if (!UnicodeScalarValidator.IsValidScalarSequence(PiiTypeIdCodec.ToStableId(canonical.PiiType))
                    || !UnicodeScalarValidator.IsValidScalarSequence(canonical.Value))
                    throw Invalid();
            }

            if (c.AliasIdentity is { } alias
                && !UnicodeScalarValidator.IsValidScalarSequence(alias.CanonicalContractToken))
                throw Invalid();
        }
    }

    private static InvalidNormativeValueDomainException Invalid() =>
        new(InvalidNormativeValueDomainException.InvalidUnicodeCode);

    // SEMANTIC_TYPE_DOMAIN_IS_CLOSED (§4.2): a stable ID outside the 21 fails check 2. Never
    // guessed, never defaulted, never accepted as a future member. The codec is the single
    // source: it throws for any value it has no hand-written entry for.
    private static bool IsKnownSemanticType(PiiType type)
    {
        try
        {
            PiiTypeIdCodec.ToStableId(type);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
