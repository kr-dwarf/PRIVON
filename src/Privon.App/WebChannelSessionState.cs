namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate 031F6I (E3) -- the observable lifecycle state of a <see cref="WebChannelSession"/>.
/// <see cref="AwaitingHello"/> is the state immediately after <see cref="WebChannelSession.Start"/>
/// succeeds, before the Hello/HelloAck handshake completes; <see cref="Accepted"/> only after the
/// HelloAck has been written and flushed successfully AND the session has been promoted by
/// <see cref="WebChannelManager.TryPromoteToAccepted"/> (Gate 031F6H R10/R11). <see cref="Closed"/> is
/// terminal and permanent.
/// </summary>
internal enum WebChannelSessionState
{
    NotStarted,
    AwaitingHello,
    Accepted,
    Closed,
}
