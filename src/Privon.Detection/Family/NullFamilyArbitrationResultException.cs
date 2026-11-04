namespace Privon.Detection.Family;

/// <summary>
/// D3B0-A A.17 §19.3 FAMILY_ARBITER_NULL_RESULT_IS_CONTRACT_VIOLATION -- a resolved and invoked
/// arbiter returned null although <see cref="IDetectorFamilyArbiter.Arbitrate"/> is declared
/// non-null. This is neither ArbitrationUnavailable, nor NoCandidate, nor an empty result, nor a D3A
/// observation, nor a RULE-U state.
///
/// Normative typed data is exactly <see cref="FamilyName"/> and <see cref="AttemptCount"/>. The
/// message prose is diagnostic and is not frozen for matching.
///
/// The violation is production-owned: only the real production boundary may raise it. The
/// constructor is internal for that reason, following the NO_EXTERNAL_RANK_MISMATCH_CONSTRUCTION
/// precedent of DetectorRankMismatchException. Privon.Detection declares no InternalsVisibleTo, so no
/// caller, harness or test can manufacture a substitute.
/// </summary>
public sealed class NullFamilyArbitrationResultException : Exception
{
    internal NullFamilyArbitrationResultException(string familyName, int attemptCount)
        : base("NullFamilyArbitrationResult(" + familyName + ")")
    {
        FamilyName = familyName;
        AttemptCount = attemptCount;
    }

    public string FamilyName { get; }

    public int AttemptCount { get; }
}
