namespace Privon.Detection.Scope;

/// <summary>
/// D3B0-A A.17 §16.7 -- what the real production boundary <see cref="UsSupportedScope.Observe"/>
/// returned for one well-formed query.
///
/// SCOPE_SUPPORT_IS_NOT_A_UNIT_AND_NOT_A_VERDICT: this is a production observation only. It is not
/// a RULE-U unit, not a RED/GREEN verdict, not a detector result and not a D3A disposition.
/// </summary>
public sealed record ScopeSupportObservation(ScopeIdentity Identity, bool IsSupported);
