namespace Privon.Detection;

/// <summary>
/// D3B0-A A.9 §17.1/§17.2 -- INTERNAL diagnostic reason only.
///
/// INTERNAL_REASON_IS_NOT_AN_EXTERNAL_IDENTITY: never serialized as a fixture failure identity,
/// never changes the ErrorType or message accepted authority freezes, never creates a sixth
/// resolver failure variant, and remains outside NORMATIVE_RESOLVER_OUTCOME. The observable
/// identity is <c>RequiredDetectorNotRegistered(&lt;id&gt;)</c> for BOTH members.
/// </summary>
public enum RequiredDetectorReason
{
    Unregistered = 0,
    NotReady = 1,
}
