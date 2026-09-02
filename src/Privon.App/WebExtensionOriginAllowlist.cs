namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate 031F6I (E3) -- the single production source of allowed Native Messaging
/// extension origins. Deliberately EMPTY in E3 (Gate 031F6H section 49): a real browser cannot yet
/// enter the production Host branch (<see cref="PrivonEntryPoint"/>) -- a future E5 gate supplies the
/// real, fixed extension origins once the store identities exist. No environment/registry/DEBUG/
/// config-file override of any kind belongs here or anywhere else in this codebase; tests inject a
/// synthetic allowlist directly through <see cref="PrivonEntryPoint.Run"/>'s own parameter, never
/// through this type.
/// </summary>
public static class WebExtensionOriginAllowlist
{
    public static IReadOnlySet<string> Production { get; } = new HashSet<string>(StringComparer.Ordinal);
}
