namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate E5G.1C -- the single verified-identity value this codebase carries for a real
/// browser-store extension: a <see cref="NativeMessagingBrowser"/> axis value paired with the
/// verified raw Store Item ID, plus the mechanically-derived Native Messaging origin
/// (<see cref="NativeMessagingOrigin"/>) that formula produces. Deliberately narrow -- no listing
/// metadata, no version/account/dashboard state, no general Store abstraction of any kind. This is
/// NOT the Web authorization allowlist (<see cref="WebExtensionOriginAllowlist.Production"/>, which
/// stays empty through this gate) -- it is only the identity a caller (Settings, via
/// <see cref="SettingsCoordinator"/>) supplies to the already-frozen
/// <see cref="NativeMessagingHostRegistrationCoordinator"/> for Chrome Native Messaging host
/// registration.
/// </summary>
internal readonly record struct VerifiedBrowserExtensionIdentity(NativeMessagingBrowser Browser, string StoreItemId)
{
    /// <summary>Mechanical derivation only -- exactly <c>"chrome-extension://" + StoreItemId + "/"</c>.
    /// No trim, no case change, no URI/path normalization, no wildcard, no query/fragment. Matches the
    /// frozen Chrome/Edge Native Messaging origin shape both browsers actually use
    /// (<see cref="NativeMessagingHostInvocation"/>'s own frozen <c>^chrome-extension://[a-p]{32}/$</c>
    /// pattern).</summary>
    internal string NativeMessagingOrigin => "chrome-extension://" + StoreItemId + "/";
}

/// <summary>
/// PRIVON 0.3.1 Gate E5G.1C -- the ONE executable production authority for verified browser-store
/// extension identity. The verified Chrome Web Store Item ID appears as a literal EXACTLY ONCE in
/// this entire codebase: here. <see cref="TryGet"/> is the only production entry point -- a caller
/// can never invent, guess, or supply an arbitrary Store ID; it can only ask whether a verified
/// identity exists for a given <see cref="NativeMessagingBrowser"/>.
///
/// CHROME_ONLY (this gate's own scope): Edge has no verified production identity yet --
/// <see cref="TryGet"/> returns <see langword="false"/> for <see cref="NativeMessagingBrowser.Edge"/>
/// and for any undefined <see cref="NativeMessagingBrowser"/> value. No fake/placeholder/wildcard
/// Edge value exists anywhere in this type, and none may ever be added merely to make this catalog
/// "complete" -- Edge identity enters this catalog only once a real Edge Store identity is itself
/// verified, exactly mirroring how Chrome's own entry was added only after Chrome's real Item ID was
/// verified.
/// </summary>
internal static class VerifiedBrowserExtensionIdentities
{
    // Verified Chrome Web Store Item ID (Gate E5G.1B commander-supplied evidence). Not a secret --
    // any Chrome Web Store listing exposes its own Item ID publicly -- but this remains the single
    // authoritative production copy; no other production source file may hold this literal.
    private const string ChromeStoreItemId = "aieobgphcpmkfnhadocdhenigmackboo";

    /// <summary>True with the verified identity for <see cref="NativeMessagingBrowser.Chrome"/>;
    /// <see langword="false"/> (with <paramref name="identity"/> left <see langword="default"/>) for
    /// every other value, including <see cref="NativeMessagingBrowser.Edge"/> and any undefined
    /// <see cref="NativeMessagingBrowser"/>.</summary>
    internal static bool TryGet(NativeMessagingBrowser browser, out VerifiedBrowserExtensionIdentity identity)
    {
        if (browser == NativeMessagingBrowser.Chrome)
        {
            identity = new VerifiedBrowserExtensionIdentity(NativeMessagingBrowser.Chrome, ChromeStoreItemId);
            return true;
        }

        identity = default;
        return false;
    }
}
