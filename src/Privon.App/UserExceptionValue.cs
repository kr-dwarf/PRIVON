using Privon.Detection;

namespace Privon.App;

/// <summary>
/// PRIVON v0.2.1 Gate 3B -- App-owned typed value for one user-registered exact-value exception,
/// mapped from a persisted <see cref="Privon.Storage.UserExceptionEntry"/>. Deliberately NOT a
/// <c>Privon.Detection</c> type (unlike the structurally similar
/// <c>AmbiguousExceptionValue</c>/<c>TrustedPublicValue</c>) -- <see cref="UserExceptionPolicyEvaluator"/>
/// is an App-owned pipeline stage, not a Detection one, so its own input shape belongs here, next
/// to it. Same "wrap (PiiType, CanonicalValue) as its own distinct type" discipline those two
/// Detection types already established, for the identical reason: a genuinely different product
/// meaning must never be interchangeable at the type level even though the shape is identical.
///
/// TRUST_VALUE_DIAGNOSTIC_SURFACE: <see cref="ToString"/> is explicitly overridden to project
/// only <see cref="PiiType"/>, never <see cref="CanonicalValue"/> -- same precedent as
/// <c>AmbiguousExceptionValue</c>/<c>TrustedPublicValue</c>.
/// </summary>
internal readonly record struct UserExceptionValue(PiiType PiiType, CanonicalValue CanonicalValue)
{
    public override string ToString() => $"{nameof(UserExceptionValue)} {{ {nameof(PiiType)} = {PiiType} }}";
}
