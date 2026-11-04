namespace Privon.Detection;

/// <summary>
/// D3B0-A A.9 §15 -- the closed NORMATIVE_RESOLVER_OUTCOME failure union: exactly
/// 3 FailureKind / 5 variants / 4 exception types. Registry, readiness, required-detector,
/// field-bag-construction and grouper-validation failures are OUTSIDE this union.
/// </summary>
public enum FailureKind
{
    ResolverSpanValidationFailure = 0,
    ResolverDomainValidationFailure = 1,
    ResolverAmbiguityFailure = 2,
}
