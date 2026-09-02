namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate E5G.P1 -- the narrow HKCU registry-leaf mechanics seam
/// <see cref="WindowsNativeMessagingHostRegistrationEnvironment"/> consumes. Exposes ONLY primitive
/// single-leaf operations addressed by an exact, caller-supplied HKCU-relative subkey path: leaf
/// existence, raw default-value read, default-value write, and single-leaf deletion.
///
/// Mirrors the established <see cref="IWindowsAutoStartRegistration"/> pattern (a narrow, App-owned
/// mechanics seam scoped to exactly what its one production consumer needs) so that consumer's
/// browser-to-leaf MAPPING can be tested against a hand-written fake instead of real HKCU access.
/// Unlike <see cref="Privon.Windows.WindowsAutoStartManager"/>, no implementation of this seam carries
/// a test-only path-override constructor: a test replaces the whole mechanics object instead, so no
/// production type here has a test switch of any kind.
///
/// STRUCTURALLY CURRENT-USER-ONLY: there is no hive parameter anywhere on this interface -- the hive
/// is fixed inside the production implementation, so neither the machine-wide hive nor any 32/64-bit
/// view selection is expressible through this seam. There is likewise no enumeration member, no
/// parent-key member, and no subtree deletion member, so "delete the NativeMessagingHosts parent" and
/// "touch a sibling host" are not expressible either.
///
/// NO JUDGMENT: this seam carries no ownership decision, no browser axis, no manifest-content
/// knowledge, and no extension/store identity of any kind. It is OS mechanics only; every policy
/// decision belongs to <see cref="NativeMessagingHostRegistrar"/> (Gate E5D, frozen).
/// </summary>
internal interface IHkcuRegistryLeafMechanics
{
    /// <summary>True only when the exact leaf at <paramref name="subKeyPath"/> currently exists.</summary>
    bool LeafExists(string subKeyPath);

    /// <summary>
    /// The leaf's DEFAULT value exactly as stored, or <see langword="null"/> when the leaf or the
    /// value is absent (or is not a string).
    ///
    /// RAW_VALUE_CONTRACT (security-critical): the returned string is never trimmed, normalized,
    /// canonicalized, case-folded, or environment-expanded. <see cref="NativeMessagingHostRegistrar"/>
    /// compares this value to its own already-expanded expected manifest path with
    /// <see cref="StringComparison.Ordinal"/>; an implementation that let the OS expand a REG_EXPAND_SZ
    /// value would let a third-party registration storing "%LOCALAPPDATA%\PRIVON\NativeMessaging\
    /// chrome-host.json" match that path exactly and be treated as PRIVON's own.
    /// </summary>
    string? GetRawDefaultValue(string subKeyPath);

    /// <summary>Sets ONLY the default value of the exact leaf at <paramref name="subKeyPath"/>,
    /// creating that leaf if it does not exist. No other value under the leaf is read or written.</summary>
    void SetDefaultValue(string subKeyPath, string value);

    /// <summary>Deletes ONLY the exact leaf at <paramref name="subKeyPath"/>. Never deletes a parent,
    /// a sibling, or a subtree. Idempotent: an already-absent leaf is a successful no-op.</summary>
    void DeleteLeaf(string subKeyPath);
}
