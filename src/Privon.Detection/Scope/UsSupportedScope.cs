namespace Privon.Detection.Scope;

/// <summary>
/// D3B0-A A.17 §16.7.2 -- the positive U.S. supported-scope domain.
///
/// SCOPE_CATALOG_IS_POSITIVE_ONLY: this type declares only what the U.S. pack supports. Non-support
/// is never stored; it is observed as non-membership of a well-formed query.
///
/// SCOPE_MEMBERSHIP_IS_INDEPENDENT_OF_REGISTRATION: the set is a literal in production source. It is
/// never computed from DetectorRegistry, IDetector instances, the rank table, any DetectorId
/// collection, PiiType, reflection or any test input. Adding or removing a detector never changes
/// it; only an accepted scope-authority amendment of §16.7 does.
/// </summary>
public static class UsSupportedScope
{
    // Exactly the 13 supported U.S. scope identities of §16.7.2.
    private static readonly HashSet<string> Supported = new(StringComparer.Ordinal)
    {
        "us.ssn",
        "us.itin",
        "us.atin",
        "us.mbi",
        "us.passport",
        "us.a_number",
        "us.i94",
        "us.sevis",
        "us.driver_state_id",
        "us.phone",
        "us.address",
        "us.name",
        "us.bank_account",
    };

    /// <summary>
    /// SCOPE_SUPPORT_IS_OBSERVED_NOT_INFERRED: the only valid source of <c>IsSupported</c>.
    /// SCOPE_MALFORMED_IS_NOT_UNSUPPORTED: <c>default(ScopeIdentity)</c> is an invalid caller argument
    /// and is never reported as <c>IsSupported = false</c>.
    /// </summary>
    public static ScopeSupportObservation Observe(ScopeIdentity identity)
    {
        if (!identity.HasValue)
            throw new ArgumentException("A default ScopeIdentity is not a scope query.", nameof(identity));

        return new ScopeSupportObservation(identity, Supported.Contains(identity.Value));
    }
}
