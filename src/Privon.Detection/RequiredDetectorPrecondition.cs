namespace Privon.Detection;

/// <summary>
/// D3B0-A A.9 §17 -- the mechanism that lets a fixture EXECUTE and FAIL for the honest declared
/// reason when a required detector does not yet exist, instead of being silently skipped.
///
/// No NoCandidate conversion, no NoUsCandidate, no skip, no fake-GREEN negative, and never
/// recorded as a satisfied semantic expectation. Only when every required detector is registered
/// AND Ready does the fixture proceed to its real oracle.
/// </summary>
public static class RequiredDetectorPrecondition
{
    /// <summary>
    /// Throws <see cref="RequiredDetectorNotRegisteredException"/> for the canonically selected
    /// failing id. Returns normally, and only then, when every required detector is registered
    /// AND Ready.
    ///
    /// <paramref name="requiredDetectorIds"/> is an IReadOnlyCollection -- deliberately NOT a
    /// list -- so no caller can imply that supply order matters.
    /// </summary>
    public static void Assert(
        IReadOnlyCollection<DetectorId> requiredDetectorIds,
        DetectorRegistry registry)
    {
        if (!TryAssert(requiredDetectorIds, registry, out var failure))
            throw new RequiredDetectorNotRegisteredException(failure.Id, failure.Reason);
    }

    /// <summary>
    /// Non-throwing form with IDENTICAL selection semantics. Returns true with
    /// <paramref name="failure"/> null on success; false with the canonically selected failure
    /// otherwise.
    /// </summary>
    public static bool TryAssert(
        IReadOnlyCollection<DetectorId> requiredDetectorIds,
        DetectorRegistry registry,
        out RequiredDetectorFailure failure)
    {
        ArgumentNullException.ThrowIfNull(requiredDetectorIds);
        ArgumentNullException.ThrowIfNull(registry);

        // §17.3 -- collect every failure first, then select canonically. Evaluating the whole
        // required set before selecting is what makes the result a set-valued function of the
        // inputs, hence permutation-invariant.
        RequiredDetectorFailure? selected = null;
        foreach (var id in requiredDetectorIds)
        {
            RequiredDetectorReason reason;
            if (!registry.TryGet(id, out var registration))
                reason = RequiredDetectorReason.Unregistered;
            else if (registration.Readiness != DetectorReadinessState.Ready)
                reason = RequiredDetectorReason.NotReady;
            else
                continue;

            // Canonical selector: smallest Id.Value under UTF-8 byte lexicographic order.
            // DetectorId.Value is unique within a registry and within any required set, so the
            // selector yields exactly one element and input permutation cannot change it.
            if (selected is null || Utf8ByteOrder.Compare(id.Value, selected.Id.Value) < 0)
                selected = new RequiredDetectorFailure(id, reason);
        }

        if (selected is null)
        {
            failure = null!;
            return true;
        }

        failure = selected;
        return false;
    }
}
