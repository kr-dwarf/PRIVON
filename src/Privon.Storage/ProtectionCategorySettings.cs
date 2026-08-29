namespace Privon.Storage;

/// <summary>PRIVON v0.2.1 Gate 3A -- persisted per-category user preference for ordinary
/// protection. Booleans only -- never carries a PII value, canonical value, or detected candidate
/// of any kind. Independent of <see cref="Privon.Core.RiskLevel.Level3"/>: a category toggle can
/// only ever affect a candidate whose base policy disposition is already
/// <c>Protect</c> -- see <c>Privon.App.CategoryPolicyEvaluator</c>'s own doc for the full
/// contract, including why Level3 can never be weakened by this type.
///
/// SCHEMA_SUPPORTED_DETECTOR_NOT_YET_IMPLEMENTED: the 5-category shape is stable and complete
/// now, deliberately ahead of detector coverage -- <see cref="PhoneEnabled"/>/
/// <see cref="EmailEnabled"/> currently gate real candidates (<c>Privon.Detection.PiiType.Phone</c>/
/// <c>.Email</c>); <see cref="NameEnabled"/>/<see cref="AddressEnabled"/>/<see cref="CompanyEnabled"/>
/// persist and round-trip correctly today but have no detector to gate yet -- no
/// <c>Privon.Detection.PiiType</c> member exists for any of the three. This is a deliberate,
/// explicit product decision (not an oversight) so a future detector's own policy wiring never
/// requires a settings-schema redesign.
/// </summary>
// [PRIVON-AI-HANDOFF]
// ROLE: Storage-owned five-category preference value; it contains booleans only, never PII.
// TRUTH: Schema may lead live detector coverage; CategoryPolicyEvaluator is authoritative for current live gating.
// FROZEN: AllOn enables every category; storage read failures resolve through the protective settings default.
// DO_NOT: Add policy, normalization, or detector behavior to this value object.
// NAVIGATE: CategoryPolicyEvaluator owns live gating; PrivonLocalStore owns persistence and fallback.
public sealed record ProtectionCategorySettings(
    bool NameEnabled,
    bool PhoneEnabled,
    bool EmailEnabled,
    bool AddressEnabled,
    bool CompanyEnabled)
{
    /// <summary>The product-contract default: every category ON. Used both as the initial value
    /// for a user who has never touched settings and as the fail-safe fallback for missing/
    /// corrupt/pre-migration persisted data -- corruption must never silently weaken protection
    /// by resolving to anything less than every category enabled.</summary>
    public static ProtectionCategorySettings AllOn => new(
        NameEnabled: true, PhoneEnabled: true, EmailEnabled: true, AddressEnabled: true, CompanyEnabled: true);
}
