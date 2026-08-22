namespace Privon.Storage;

/// <summary>General on/off and UX preference settings (audit contract section 6). Minimal
/// shape for Phase 1 -- extended once the UX layer is built.</summary>
public sealed record PrivonSettings(bool ProtectionEnabled, DateTimeOffset? PausedUntilUtc = null)
{
    /// <summary>Used whenever settings cannot be read (missing or corrupted). Protection
    /// stays enabled by default -- corruption must never silently weaken protection.</summary>
    public static PrivonSettings SafeDefault => new(ProtectionEnabled: true, PausedUntilUtc: null);
}
