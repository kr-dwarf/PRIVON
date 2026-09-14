using System.IO;

namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate E5G.P1 -- the only production implementation of the frozen
/// <see cref="INativeMessagingHostRegistrationEnvironment"/> (Gate E5D). Supplies the real Windows
/// mechanics that interface describes and nothing else: it maps the browser axis to its ONE exact
/// registry leaf, delegates every registry primitive to <see cref="IHkcuRegistryLeafMechanics"/>, and
/// performs manifest file access directly at the exact caller-supplied path.
///
/// NO POLICY (the whole point of this type): every ownership judgment
/// <see cref="NativeMessagingHostRegistrar"/> makes -- what counts as PRIVON's own registration, what
/// is a third party's, what a missing manifest means, and the frozen mutation ORDER of install and
/// uninstall -- stays entirely in that registrar (Gate E5D, frozen and untouched by this gate). This
/// type carries no such judgment, no manifest-content knowledge, no host executable semantics, and no
/// browser-extension or store identity of any kind.
///
/// EXACT LEAVES ONLY (Gate E5C, frozen): Chrome and Edge each map to exactly one hardcoded
/// HKCU-relative leaf below. There is no fallback location, no prefix or wildcard search, no sibling
/// enumeration, and no parent-key access anywhere in this type -- and none is expressible through
/// <see cref="IHkcuRegistryLeafMechanics"/> either, which has no hive parameter, no enumeration
/// member, and no subtree-deletion member. An undefined <see cref="NativeMessagingBrowser"/> value
/// fails closed in <see cref="LeafSubKeyPath"/> BEFORE any mechanics call is made.
///
/// Constructing this type performs no registry read, registry write, or filesystem access. The
/// Settings registration coordinator uses it for read-only inspection and for explicit, policy-gated
/// user Provision/Repair actions; startup construction alone does not mutate registration state.
/// </summary>
internal sealed class WindowsNativeMessagingHostRegistrationEnvironment : INativeMessagingHostRegistrationEnvironment
{
    internal const string ChromeLeafSubKeyPath = @"Software\Google\Chrome\NativeMessagingHosts\com.privon.host";
    internal const string EdgeLeafSubKeyPath = @"Software\Microsoft\Edge\NativeMessagingHosts\com.privon.host";

    private readonly IHkcuRegistryLeafMechanics _registry;

    public WindowsNativeMessagingHostRegistrationEnvironment()
        : this(new HkcuRegistryLeafMechanics())
    {
    }

    internal WindowsNativeMessagingHostRegistrationEnvironment(IHkcuRegistryLeafMechanics registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    /// <summary>The ONE exact HKCU-relative leaf for <paramref name="browser"/>. Any value outside the
    /// declared enum fails closed here, before this type reaches the registry at all.</summary>
    private static string LeafSubKeyPath(NativeMessagingBrowser browser) => browser switch
    {
        NativeMessagingBrowser.Chrome => ChromeLeafSubKeyPath,
        NativeMessagingBrowser.Edge => EdgeLeafSubKeyPath,
        _ => throw new ArgumentOutOfRangeException(nameof(browser), browser, "Unrecognized NativeMessagingBrowser value."),
    };

    // ==================================================================
    // Registry primitives -- mapping plus a single delegation each.
    // ==================================================================

    public bool SubkeyExists(NativeMessagingBrowser browser) =>
        _registry.LeafExists(LeafSubKeyPath(browser));

    /// <summary>Returns the leaf's default value exactly as stored -- see
    /// <see cref="IHkcuRegistryLeafMechanics.GetRawDefaultValue"/>'s RAW_VALUE_CONTRACT. This method
    /// deliberately adds no post-processing of its own: no trim, no normalization, no case change, and
    /// no path resolution, so the registrar's frozen <see cref="StringComparison.Ordinal"/> comparison
    /// sees precisely what the registry holds.</summary>
    public string? GetSubkeyDefaultValue(NativeMessagingBrowser browser) =>
        _registry.GetRawDefaultValue(LeafSubKeyPath(browser));

    public void SetSubkeyDefaultValue(NativeMessagingBrowser browser, string manifestPath)
    {
        ArgumentNullException.ThrowIfNull(manifestPath);
        _registry.SetDefaultValue(LeafSubKeyPath(browser), manifestPath);
    }

    public void DeleteSubkey(NativeMessagingBrowser browser) =>
        _registry.DeleteLeaf(LeafSubKeyPath(browser));

    // ==================================================================
    // Manifest primitives -- exact supplied path only. The path itself is registrar-owned (Gate E5D.B's
    // frozen formula); this type never derives, guesses, or rewrites one, and never inspects a
    // filename or directory to infer anything about it.
    // ==================================================================

    public bool ManifestExists(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return File.Exists(path);
    }

    public string? ReadManifest(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>Writes <paramref name="content"/> to exactly <paramref name="path"/>, first ensuring
    /// that path's own containing directory chain exists. That is creation readiness for a fresh
    /// install, never a claim over the directory: nothing here ever deletes, prunes, enumerates, or
    /// otherwise treats that directory as PRIVON's to manage.</summary>
    public void WriteManifest(string path, string content)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(content);

        string? containingDirectory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(containingDirectory))
            Directory.CreateDirectory(containingDirectory);

        File.WriteAllText(path, content);
    }

    /// <summary>Deletes exactly the one supplied file. Never touches the containing directory, a
    /// sibling file, or any pattern-matched set. Idempotent: an already-absent file is a no-op.</summary>
    public void DeleteManifest(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        File.Delete(path);
    }
}
