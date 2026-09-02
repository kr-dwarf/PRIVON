namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate 031F6L (E4) -- the closed set of mechanical outcomes a decision-time
/// ChallengeRequest/ChallengeResponse round trip can produce (see <see cref="WebChannelSession.ChallengeAsync"/>).
/// Mechanical only -- carries no product/browser/origin policy of any kind.
/// </summary>
internal enum WebChallengeOutcome
{
    Success,
    Busy,
    NotAccepted,
    WriteFailed,
    TimedOut,
    SessionClosed
}
