using Microsoft.Win32;

namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate E5G.P1 -- the only production implementation of
/// <see cref="IHkcuRegistryLeafMechanics"/>. Mechanical single-leaf registry access under the current
/// user's hive, addressed by an exact caller-supplied relative subkey path.
///
/// Deliberately generic: it hardcodes no browser, no host name, and no PRIVON literal of any kind
/// (WINDOWS_VS_APP_RESPONSIBILITY, matching the established <see cref="Privon.Windows.WindowsAutoStartManager"/>
/// precedent -- mechanics know OS facts only; which exact leaf those facts mean anything for is
/// <see cref="WindowsNativeMessagingHostRegistrationEnvironment"/>'s decision).
///
/// CURRENT_USER_ONLY: every operation below targets <see cref="Registry.CurrentUser"/> exclusively.
/// The hive is fixed in code and is not a parameter anywhere, so the machine-wide hive is not
/// reachable through this type; there is likewise no 32/64-bit view selection, no key enumeration, no
/// parent-key access, and no subtree deletion member.
///
/// EXCEPTIONS PROPAGATE: unlike <see cref="Privon.Windows.WindowsAutoStartManager"/> (whose members
/// return <see cref="bool"/> for a UI settings toggle and therefore resolve failure to a typed
/// <see langword="false"/>), the mutating members here return <see langword="void"/> -- there is no
/// channel through which a swallowed failure could be reported. A silently-swallowed write or delete
/// would look to <see cref="NativeMessagingHostRegistrar"/> exactly like a successful one, so failures
/// are allowed to propagate instead; that registrar's own frozen mutation ORDER already guarantees a
/// mid-operation failure lands on a state its ownership contract can still classify and recover.
/// Failure POLICY belongs to whichever future gate wires registration, never to this type.
/// </summary>
internal sealed class HkcuRegistryLeafMechanics : IHkcuRegistryLeafMechanics
{
    public bool LeafExists(string subKeyPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(subKeyPath);

        using var key = Registry.CurrentUser.OpenSubKey(subKeyPath, writable: false);
        return key is not null;
    }

    /// <summary>
    /// RAW_VALUE_CONTRACT (security-critical -- see <see cref="IHkcuRegistryLeafMechanics.GetRawDefaultValue"/>):
    /// <see cref="RegistryValueOptions.DoNotExpandEnvironmentNames"/> is passed explicitly because
    /// Microsoft.Win32's default (<see cref="RegistryValueOptions.None"/>) DOES expand a REG_EXPAND_SZ
    /// value. The returned string is handed back exactly as stored -- no trim, no normalization, no
    /// canonicalization, no case conversion, and no path resolution of any kind.
    /// </summary>
    public string? GetRawDefaultValue(string subKeyPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(subKeyPath);

        using var key = Registry.CurrentUser.OpenSubKey(subKeyPath, writable: false);
        return key?.GetValue(null, defaultValue: null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    /// <summary>Writes <paramref name="value"/> as the leaf's default value, as an ordinary
    /// <see cref="RegistryValueKind.String"/> (REG_SZ, never REG_EXPAND_SZ) so a later
    /// <see cref="GetRawDefaultValue"/> read returns it byte-for-byte. Creates the exact leaf (and any
    /// missing intermediate key on the way to it) when absent; no other value is read or written.</summary>
    public void SetDefaultValue(string subKeyPath, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(subKeyPath);
        ArgumentNullException.ThrowIfNull(value);

        using var key = Registry.CurrentUser.CreateSubKey(subKeyPath, writable: true);
        key.SetValue(null, value, RegistryValueKind.String);
    }

    /// <summary>Deletes the exact leaf only. <see cref="RegistryKey.DeleteSubKey(string, bool)"/> is
    /// the non-tree overload: it removes a single key and throws if that key still has child keys, so
    /// a subtree can never be removed here even accidentally. Passing
    /// <c>throwOnMissingSubKey: false</c> makes an already-absent leaf a successful no-op.</summary>
    public void DeleteLeaf(string subKeyPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(subKeyPath);

        Registry.CurrentUser.DeleteSubKey(subKeyPath, throwOnMissingSubKey: false);
    }
}
