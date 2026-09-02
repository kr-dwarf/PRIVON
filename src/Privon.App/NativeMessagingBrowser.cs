namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate E5D -- the browser axis for the production Native Messaging host registration
/// contract (Gate E5C). A DELIBERATELY SEPARATE concept from <see cref="SupportedWebTarget"/>, which
/// enumerates AI web SERVICES (ChatGptWeb/ClaudeWeb/GeminiWeb/GrokWeb/DeepSeekWeb) -- this type
/// enumerates BROWSERS (Chrome/Edge) instead, an orthogonal axis. Never merge the two: the
/// registration contract this type serves has no concept of AI service identity at all -- it only
/// ever registers/unregisters a Native Messaging host per browser, independent of which AI web
/// target a page happens to be.
///
/// Declared starting at 1 (never 0), mirroring this codebase's own "safe value first" discipline
/// (<see cref="SupportedWebTarget"/>, <see cref="SupportedTarget"/>): so <c>default(NativeMessagingBrowser)</c>
/// is not a defined member.
/// </summary>
internal enum NativeMessagingBrowser
{
    Chrome = 1,
    Edge = 2,
}
