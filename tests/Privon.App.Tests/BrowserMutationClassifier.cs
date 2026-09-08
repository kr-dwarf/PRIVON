using AppBrowser = Privon.App.NativeMessagingBrowser;

namespace Privon.App.Tests;

// PRIVON 0.3.1 Gate E5G.1F audit remediation -- the ONE precise test-side classifier answering "does
// this FakeNativeMessagingHostRegistrationEnvironment.CallLog entry represent an actual MUTATION of
// the given browser's registration state?"
//
// WHY THIS EXISTS (the exact defect it closes): FakeNativeMessagingHostRegistrationEnvironment logs
// registry entries with the browser's own enum ToString() as an argument -- e.g.
// "SetSubkeyDefaultValue(Chrome, ...)"/"DeleteSubkey(Edge)" -- but manifest entries carry ONLY the
// manifest PATH -- "WriteManifest(C:\...\chrome-host.json)"/"DeleteManifest(...)" -- no browser name
// at all. NativeMessagingHostRegistrationLayout's own filenames are lowercase
// ("chrome-host.json"/"edge-host.json"), so a predicate that only ever looks for the literal
// substring "Chrome"/"Edge" (Ordinal, case-sensitive) NEVER matches a manifest mutation -- silently
// making the manifest half of any "zero cross-browser mutation" assertion built that way vacuously
// true regardless of what the manifest side actually did. This type classifies registry entries by
// the browser argument they actually carry, and manifest entries by an exact (OrdinalIgnoreCase,
// since Windows paths are case-insensitive) comparison against that browser's OWN
// NativeMessagingHostRegistrationLayout.ExpectedManifestPath -- never a substring guess.
//
// Shared (not duplicated) across the Chrome and Edge gate test files precisely because a subtly
// wrong copy in only one of them is exactly the kind of silent divergence this remediation exists to
// eliminate.
internal static class BrowserMutationClassifier
{
    /// <summary>True only for a WRITE primitive (SetSubkeyDefaultValue/DeleteSubkey/WriteManifest/
    /// DeleteManifest) that targets EXACTLY <paramref name="browser"/> -- registry entries by their
    /// own embedded browser argument, manifest entries by an exact path match against
    /// <paramref name="browser"/>'s own expected manifest path. Every read primitive
    /// (SubkeyExists/GetSubkeyDefaultValue/ManifestExists/ReadManifest) is never a mutation for any
    /// browser, regardless of which browser or path it names. Exactly the logical OR of the four
    /// individually-nameable predicates below -- a caller wanting to prove each operation kind
    /// independently zero (rather than only their combined total) should use those four directly.</summary>
    internal static bool IsMutationFor(string logEntry, AppBrowser browser) =>
        IsSetSubkeyDefaultValueFor(logEntry, browser)
        || IsDeleteSubkeyFor(logEntry, browser)
        || IsWriteManifestFor(logEntry, browser)
        || IsDeleteManifestFor(logEntry, browser);

    internal static bool IsSetSubkeyDefaultValueFor(string logEntry, AppBrowser browser) =>
        logEntry.StartsWith($"SetSubkeyDefaultValue({browser}, ", StringComparison.Ordinal);

    internal static bool IsDeleteSubkeyFor(string logEntry, AppBrowser browser) =>
        string.Equals(logEntry, $"DeleteSubkey({browser})", StringComparison.Ordinal);

    internal static bool IsWriteManifestFor(string logEntry, AppBrowser browser) =>
        ExtractArgument(logEntry, "WriteManifest") is string path
        && string.Equals(path, NativeMessagingHostRegistrationLayout.ExpectedManifestPath(browser), StringComparison.OrdinalIgnoreCase);

    internal static bool IsDeleteManifestFor(string logEntry, AppBrowser browser) =>
        ExtractArgument(logEntry, "DeleteManifest") is string path
        && string.Equals(path, NativeMessagingHostRegistrationLayout.ExpectedManifestPath(browser), StringComparison.OrdinalIgnoreCase);

    // Parses the exact "<operation>(<argument>)" shape every FakeNativeMessagingHostRegistrationEnvironment
    // CallLog entry uses -- a plain substring extraction, never a regex, matching that fake's own
    // trivial $"{nameof(Member)}({args})" logging format exactly. Windows paths never contain a ')'
    // character, so the final ')' is always the argument's own closing delimiter.
    private static string? ExtractArgument(string logEntry, string operation)
    {
        string prefix = operation + "(";
        if (!logEntry.StartsWith(prefix, StringComparison.Ordinal) || !logEntry.EndsWith(")", StringComparison.Ordinal))
            return null;

        return logEntry.Substring(prefix.Length, logEntry.Length - prefix.Length - 1);
    }
}
