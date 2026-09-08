namespace Privon.App;

/// <summary>
/// The single production authority for the current release's browser scope. PRIVON 0.3.1 supports
/// Chrome and deliberately defers Edge. Browser identity, Store identity, and dormant registration
/// mechanics remain owned by their existing types so a future release can reactivate Edge without
/// treating this release-scoped decision as a permanent unsupported declaration.
/// </summary>
internal static class ReleaseBrowserSupportPolicy
{
    internal static bool IsSupported(NativeMessagingBrowser browser) => browser switch
    {
        NativeMessagingBrowser.Chrome => true,
        NativeMessagingBrowser.Edge => false,
        _ => false,
    };
}
