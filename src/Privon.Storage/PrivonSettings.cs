namespace Privon.Storage;

/// <summary>General on/off and UX preference settings (audit contract section 6). Minimal
/// shape for Phase 1, extended in PRIVON v0.2.1 Gate 3A with <see cref="Categories"/>.
///
/// <see cref="Categories"/> is deliberately nullable here (never in the type itself --
/// <see cref="ProtectionCategorySettings"/> has no nullable member) purely so old persisted JSON
/// that predates this property (or a hand-constructed <see cref="PrivonSettings"/> that never set
/// it) can deserialize without a required-property failure. A null value is NEVER a legitimate
/// end state that reaches outside <c>Privon.Storage</c> -- <see cref="PrivonLocalStore.LoadSettings"/>
/// resolves it to <see cref="ProtectionCategorySettings.AllOn"/> before returning (see that
/// method's own BACKWARD_COMPATIBILITY doc); direct callers of this constructor that care about
/// the category value should always supply it explicitly.</summary>
public sealed record PrivonSettings(bool ProtectionEnabled, DateTimeOffset? PausedUntilUtc = null, ProtectionCategorySettings? Categories = null)
{
    /// <summary>Used whenever settings cannot be read (missing or corrupted). Protection
    /// stays enabled by default -- corruption must never silently weaken protection. Categories
    /// likewise always resolves to <see cref="ProtectionCategorySettings.AllOn"/> here, never a
    /// partial or empty set.</summary>
    public static PrivonSettings SafeDefault => new(ProtectionEnabled: true, PausedUntilUtc: null, Categories: ProtectionCategorySettings.AllOn);
}
