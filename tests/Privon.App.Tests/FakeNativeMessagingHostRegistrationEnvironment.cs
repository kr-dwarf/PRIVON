using AppBrowser = Privon.App.NativeMessagingBrowser;

namespace Privon.App.Tests;

/// <summary>
/// PRIVON 0.3.1 Gate E5G.P3 -- fully in-memory, hand-written implementation of the FROZEN production
/// <see cref="INativeMessagingHostRegistrationEnvironment"/> (Gate E5D). No real HKCU or filesystem
/// access of any kind. Unlike the earlier Gate E5C/E5D/E5G.P1 fakes, this one implements the
/// production interface DIRECTLY (compile-time, via <c>InternalsVisibleTo</c>) rather than through a
/// DispatchProxy stub -- the interface and its `AppBrowser` axis already exist in
/// production by this gate, so no reflection-based not-yet-existing-member dance is needed.
///
/// Mirrors the established <see cref="FakeHostRegistrationEnvironment"/>/
/// <see cref="Gate031E5GP1_RegistrationEnvironmentTestHarness.FakeRegistryLeafMechanics"/> pattern:
/// a full call log for exact-mutation-count assertions, Seed*/​*At members for test setup and
/// post-hoc verification that never go through the type-under-test's own reachable surface, and
/// optional per-member exception injection so a test can prove a coordinator never turns a thrown
/// registrar/environment exception into a false success (Gate E5G.P3 case 25).
/// </summary>
internal sealed class FakeNativeMessagingHostRegistrationEnvironment : INativeMessagingHostRegistrationEnvironment
{
    private readonly Dictionary<AppBrowser, string?> _subkeys = [];
    private readonly Dictionary<string, string> _manifests = new(StringComparer.Ordinal);

    public List<string> CallLog { get; } = [];

    public Exception? ThrowOnSetSubkeyDefaultValue { get; set; }
    public Exception? ThrowOnWriteManifest { get; set; }
    public Exception? ThrowOnDeleteSubkey { get; set; }
    public Exception? ThrowOnDeleteManifest { get; set; }

    /// <summary>One-shot mechanical scripting hook (Gate E5G.P3.B): when set, the NEXT
    /// <see cref="DeleteManifest"/> call is logged as usual but does NOT remove the manifest --
    /// simulating a delete that mechanically no-ops (e.g. the file is externally recreated /
    /// survives) rather than throwing. Auto-resets to <see langword="false"/> after that one call.
    /// Pure OS-mechanics simulation only -- carries no ownership/orphan judgment of its own.</summary>
    public bool SuppressNextDeleteManifest { get; set; }

    // ---- Seeding / post-hoc verification (test-side only) ----

    public void SeedLeaf(AppBrowser browser, string? defaultValue) => _subkeys[browser] = defaultValue;

    public void SeedManifest(string path, string content) => _manifests[path] = content;

    public bool LeafPresent(AppBrowser browser) => _subkeys.ContainsKey(browser);

    public string? LeafValue(AppBrowser browser) => _subkeys.TryGetValue(browser, out var value) ? value : null;

    public bool ManifestPresent(string path) => _manifests.ContainsKey(path);

    public string? ManifestContentAt(string path) => _manifests.TryGetValue(path, out var value) ? value : null;

    // ---- INativeMessagingHostRegistrationEnvironment ----

    public bool SubkeyExists(AppBrowser browser)
    {
        CallLog.Add($"{nameof(SubkeyExists)}({browser})");
        return _subkeys.ContainsKey(browser);
    }

    public string? GetSubkeyDefaultValue(AppBrowser browser)
    {
        CallLog.Add($"{nameof(GetSubkeyDefaultValue)}({browser})");
        return _subkeys.TryGetValue(browser, out var value) ? value : null;
    }

    public void SetSubkeyDefaultValue(AppBrowser browser, string manifestPath)
    {
        CallLog.Add($"{nameof(SetSubkeyDefaultValue)}({browser}, {manifestPath})");
        if (ThrowOnSetSubkeyDefaultValue is not null) throw ThrowOnSetSubkeyDefaultValue;
        _subkeys[browser] = manifestPath;
    }

    public void DeleteSubkey(AppBrowser browser)
    {
        CallLog.Add($"{nameof(DeleteSubkey)}({browser})");
        if (ThrowOnDeleteSubkey is not null) throw ThrowOnDeleteSubkey;
        _subkeys.Remove(browser);
    }

    public bool ManifestExists(string path)
    {
        CallLog.Add($"{nameof(ManifestExists)}({path})");
        return _manifests.ContainsKey(path);
    }

    public string? ReadManifest(string path)
    {
        CallLog.Add($"{nameof(ReadManifest)}({path})");
        return _manifests.TryGetValue(path, out var content) ? content : null;
    }

    public void WriteManifest(string path, string content)
    {
        CallLog.Add($"{nameof(WriteManifest)}({path})");
        if (ThrowOnWriteManifest is not null) throw ThrowOnWriteManifest;
        _manifests[path] = content;
    }

    public void DeleteManifest(string path)
    {
        CallLog.Add($"{nameof(DeleteManifest)}({path})");
        if (ThrowOnDeleteManifest is not null) throw ThrowOnDeleteManifest;
        if (SuppressNextDeleteManifest)
        {
            SuppressNextDeleteManifest = false;
            return;
        }
        _manifests.Remove(path);
    }
}
