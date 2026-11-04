namespace Privon.Detection;

/// <summary>
/// D3B0-A A.9 §17.1/§17.2 -- the ACCEPTED, FROZEN observable fixture failure identity.
///
/// REQUIRED_DETECTOR_FAILURE_IDENTITY_IS_FROZEN: the observable failure is exactly
/// <c>RequiredDetectorNotRegistered(&lt;id&gt;)</c> for BOTH conditions -- unregistered, and
/// registered but not Ready. Accepted authority (A1 §1.1 / A1.1 §4) freezes ONE identity for
/// both. There is deliberately NO ErrorCode: accepted authority defines the identity by exception
/// name plus &lt;id&gt;.
///
/// PLACEMENT: a fixture/harness precondition failure, NOT a resolver-call outcome. It is not part
/// of NORMATIVE_RESOLVER_OUTCOME; the union stays 3 / 5 / 4 (§15).
///
/// PRIVACY: the payload carries only a frozen contract DetectorId -- no canonical value, alias
/// token, raw text, detector display name, or candidate identity.
/// </summary>
public sealed class RequiredDetectorNotRegisteredException : Exception
{
    private readonly string _message;

    internal RequiredDetectorNotRegisteredException(DetectorId id, RequiredDetectorReason reason)
        : base(BuildMessage(id))
    {
        Id = id;
        Reason = reason;
        _message = BuildMessage(id);
    }

    /// <summary>The canonically selected failing required detector (§17.3).</summary>
    public DetectorId Id { get; }

    /// <summary>
    /// DIAGNOSTICS ONLY -- not part of the frozen identity
    /// (INTERNAL_REASON_IS_NOT_AN_EXTERNAL_IDENTITY, §17.1). It appears in no normative message,
    /// no ErrorCode, and no serialized outcome.
    /// </summary>
    public RequiredDetectorReason Reason { get; }

    /// <summary>
    /// Exactly <c>RequiredDetectorNotRegistered(&lt;Id.Value&gt;)</c>, invariant culture, no
    /// other text -- and notably no <c>kind=</c> segment, which A.1 added and A.2 withdrew.
    /// Overridden so the framework can never append to it.
    /// </summary>
    public override string Message => _message;

    private static string BuildMessage(DetectorId id) =>
        "RequiredDetectorNotRegistered(" + id.ToString() + ")";
}

/// <summary>D3B0-A A.9 §17.2 -- the non-throwing form's selected failure.</summary>
public sealed record RequiredDetectorFailure(DetectorId Id, RequiredDetectorReason Reason);
