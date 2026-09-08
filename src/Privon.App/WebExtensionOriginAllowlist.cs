namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate E5G.3 -- the single production source of allowed Native Messaging extension
/// origins. Authorizes exactly the verified Chrome Web Store origin for this Chrome-only 0.3.1
/// release (<see cref="ReleaseBrowserSupportPolicy"/>), mechanically derived from
/// <see cref="VerifiedBrowserExtensionIdentities"/>'s own Chrome entry -- never a second,
/// independently-literal copy of the Store ID/origin. The Edge Store origin stays absent (Edge
/// remains deferred for 0.3.1) and no other origin is ever added. No environment/registry/DEBUG/
/// config-file override of any kind belongs here or anywhere else in this codebase; tests inject a
/// synthetic allowlist directly through <see cref="PrivonEntryPoint.Run"/>'s own parameter, never
/// through this type.
/// </summary>
public static class WebExtensionOriginAllowlist
{
    public static IReadOnlySet<string> Production { get; } = BuildProduction();

    private static IReadOnlySet<string> BuildProduction()
    {
        VerifiedBrowserExtensionIdentities.TryGet(NativeMessagingBrowser.Chrome, out var chrome);
        return new HashSet<string>(new[] { chrome.NativeMessagingOrigin }, StringComparer.Ordinal);
    }
}
