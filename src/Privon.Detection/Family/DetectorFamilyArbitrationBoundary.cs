namespace Privon.Detection.Family;

/// <summary>
/// D3B0-A A.17 §19.1 -- the real production family-arbitration boundary. It adds observability
/// only and no arbitration behaviour.
///
/// FAMILY_UNAVAILABLE_PRODUCES_NO_RESULT (§19.3) -- exactly two normal combinations:
///   returns false  ⇔ State == ArbitrationUnavailable ⇔ result is null;
///   returns true   ⇔ State == ArbitrationAvailable   ⇔ result is the real arbiter's own result.
/// A resolved arbiter that returns null is not a third tuple: the boundary does not return normally
/// and raises <see cref="NullFamilyArbitrationResultException"/>.
///
/// FAMILY_ATTEMPTS_ARE_INPUTS (§19.3): attempts are passed through unread. An attempt's DetectorId is
/// never resolved against the registry and never becomes a RequiredDetectorIds member.
/// </summary>
public static class DetectorFamilyArbitrationBoundary
{
    public static bool TryArbitrate(
        IDetectorFamilyArbiterProvider provider, string familyName,
        IReadOnlyList<FamilyCandidateAttempt> attempts,
        out FamilyArbitrationResult? result, out FamilyArbitrationObservation observation)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(familyName);
        ArgumentNullException.ThrowIfNull(attempts);

        int attemptCount = attempts.Count;

        if (!provider.TryGetArbiter(familyName, out var arbiter))
        {
            result = null;
            observation = new FamilyArbitrationObservation(
                familyName, FamilyArbitrationState.ArbitrationUnavailable, attemptCount);
            return false;
        }

        // FAMILY_SAME_ARBITER: the resolved arbiter's own Arbitrate is invoked; nothing here decides
        // emission, winners or rejections.
        FamilyArbitrationResult? produced = arbiter.Arbitrate(attempts);
        if (produced is null)
            throw new NullFamilyArbitrationResultException(familyName, attemptCount);

        result = produced;
        observation = new FamilyArbitrationObservation(
            familyName, FamilyArbitrationState.ArbitrationAvailable, attemptCount);
        return true;
    }
}
