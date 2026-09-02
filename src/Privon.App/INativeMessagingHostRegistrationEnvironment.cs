namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate E5D -- the narrow registry/filesystem mechanics seam
/// <see cref="NativeMessagingHostRegistrar"/> consumes for the production Native Messaging host
/// registration contract (Gate E5C). Exposes ONLY primitive registry-leaf and manifest-file
/// operations -- exact leaf existence, exact default-value read/write, exact leaf deletion, exact
/// manifest existence/read/write/deletion -- and carries no OWNED/STALE/FOREIGN/orphan judgment of
/// any kind; every such decision belongs exclusively to <see cref="NativeMessagingHostRegistrar"/>
/// itself. Mirrors the established <see cref="IWindowsAutoStartRegistration"/> pattern: a narrow,
/// App-owned seam scoped to exactly what its one production consumer needs, so that consumer's
/// POLICY can be tested against a hand-written fake instead of real HKCU/filesystem access.
///
/// Structurally cannot reach HKLM, WOW6432Node, the NativeMessagingHosts parent key, or a sibling
/// subkey -- every member here is scoped to exactly ONE leaf subkey (identified only by
/// <see cref="NativeMessagingBrowser"/>) and exactly ONE manifest path (an arbitrary caller-supplied
/// string) at a time; there is no enumeration member, no parent-key member, and no cross-browser
/// member anywhere on this interface.
/// </summary>
internal interface INativeMessagingHostRegistrationEnvironment
{
    bool SubkeyExists(NativeMessagingBrowser browser);
    string? GetSubkeyDefaultValue(NativeMessagingBrowser browser);
    void SetSubkeyDefaultValue(NativeMessagingBrowser browser, string manifestPath);
    void DeleteSubkey(NativeMessagingBrowser browser);

    bool ManifestExists(string path);
    string? ReadManifest(string path);
    void WriteManifest(string path, string content);
    void DeleteManifest(string path);
}
