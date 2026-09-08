namespace Privon.App.Tests;

/// <summary>
/// PRIVON 0.3.1 Gate E5C -- RED-only. The smallest test-side seam a future production Native
/// Messaging host registrar (Gate E5D, not yet built) would need: registry mutation for exactly ONE
/// leaf subkey per browser (never HKLM, never WOW6432Node, never the NativeMessagingHosts parent,
/// never a sibling subkey), plus manifest-file mutation at an arbitrary path.
///
/// Structurally prevents the mutations the frozen registration contract forbids: there is no method
/// here that could ever delete a parent key, enumerate/delete a sibling subkey, or reach HKLM/
/// WOW6432Node -- if a hypothetical registrar wanted to do any of those things through this seam,
/// there is simply no way to express it. Sibling subkeys and manifests are tracked SEPARATELY (via
/// the Seed*/*Unchanged members below) precisely so a test can prove the type under test never
/// touched them, without that type ever being handed a way to reach them in the first place.
///
/// Mirrors the existing <see cref="IWindowsAutoStartRegistration"/>/
/// <see cref="FakeWindowsAutoStartRegistration"/> pattern (narrow, hand-written, fully in-memory
/// fake -- no real OS access of any kind) rather than <c>WindowsAutoStartManagerTests</c>'s real-
/// HKCU-scratch-key pattern, per this gate's own explicit "no real registry mutation" requirement.
///
/// TEST-ONLY scaffolding: no production type in src/ implements <see cref="IHostRegistrationEnvironment"/>.
/// Gate E5C is RED-only and adds no production code of any kind -- see
/// Gate031E5C_NativeMessagingHostRegistrarRedTests.cs's own header for how this fake is used.
/// </summary>
internal enum NativeMessagingBrowser
{
    Chrome = 1,
    Edge = 2,
}

internal interface IHostRegistrationEnvironment
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

internal sealed class FakeHostRegistrationEnvironment : IHostRegistrationEnvironment
{
    private readonly Dictionary<NativeMessagingBrowser, string?> _subkeys = [];
    private readonly Dictionary<NativeMessagingBrowser, Dictionary<string, string>> _siblingSubkeys = new()
    {
        [NativeMessagingBrowser.Chrome] = new(StringComparer.OrdinalIgnoreCase),
        [NativeMessagingBrowser.Edge] = new(StringComparer.OrdinalIgnoreCase),
    };
    private readonly Dictionary<string, string> _manifests = new(StringComparer.OrdinalIgnoreCase);

    public List<string> CallLog { get; } = [];

    // ---- Seeding (test setup only -- never reachable by the type under test) ----

    public void SeedOwned(NativeMessagingBrowser browser, string manifestPath, string manifestContent)
    {
        _subkeys[browser] = manifestPath;
        _manifests[manifestPath] = manifestContent;
    }

    public void SeedForeign(NativeMessagingBrowser browser, string foreignManifestPath, string foreignManifestContent)
    {
        _subkeys[browser] = foreignManifestPath;
        _manifests[foreignManifestPath] = foreignManifestContent;
    }

    /// <summary>Commander-frozen STALE (Gate E5C.1): the leaf's default value equals
    /// <paramref name="manifestPath"/> exactly (the same witness OWNED uses), but -- unlike
    /// <see cref="SeedOwned"/> -- no manifest is written at that path, matching the frozen
    /// definition's fourth condition ("the expected manifest is ABSENT").</summary>
    public void SeedStale(NativeMessagingBrowser browser, string manifestPath) =>
        _subkeys[browser] = manifestPath;

    public void SeedSibling(NativeMessagingBrowser browser, string siblingHostName, string siblingDefaultValue) =>
        _siblingSubkeys[browser][siblingHostName] = siblingDefaultValue;

    public void SeedManifest(string path, string content) => _manifests[path] = content;

    // ---- Post-hoc verification (test assertions only -- reads state the type under test never
    // received a way to reach) ----

    public bool SiblingSubkeyUnchanged(NativeMessagingBrowser browser, string siblingHostName, string expectedDefaultValue) =>
        _siblingSubkeys[browser].TryGetValue(siblingHostName, out var value)
        && string.Equals(value, expectedDefaultValue, StringComparison.Ordinal);

    public bool ManifestUnchanged(string path, string expectedContent) =>
        _manifests.TryGetValue(path, out var content) && string.Equals(content, expectedContent, StringComparison.Ordinal);

    // ---- IHostRegistrationEnvironment (the seam a registrar under test would consume) ----

    public bool SubkeyExists(NativeMessagingBrowser browser)
    {
        CallLog.Add($"{nameof(SubkeyExists)}({browser})");
        return _subkeys.ContainsKey(browser);
    }

    public string? GetSubkeyDefaultValue(NativeMessagingBrowser browser)
    {
        CallLog.Add($"{nameof(GetSubkeyDefaultValue)}({browser})");
        return _subkeys.TryGetValue(browser, out var value) ? value : null;
    }

    public void SetSubkeyDefaultValue(NativeMessagingBrowser browser, string manifestPath)
    {
        CallLog.Add($"{nameof(SetSubkeyDefaultValue)}({browser}, {manifestPath})");
        _subkeys[browser] = manifestPath;
    }

    public void DeleteSubkey(NativeMessagingBrowser browser)
    {
        CallLog.Add($"{nameof(DeleteSubkey)}({browser})");
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
        _manifests[path] = content;
    }

    public void DeleteManifest(string path)
    {
        CallLog.Add($"{nameof(DeleteManifest)}({path})");
        _manifests.Remove(path);
    }
}
