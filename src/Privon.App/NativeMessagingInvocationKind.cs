namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate 031F6I (E3) -- the three mutually exclusive process-launch classifications
/// <see cref="NativeMessagingHostInvocation.Classify"/> can ever produce. A normal (zero-argument)
/// launch is <see cref="Normal"/>; a well-formed, allowlisted Chrome/Edge Native Messaging host
/// invocation is <see cref="Host"/>; anything host-shaped but malformed or not allowlisted is
/// <see cref="RejectedHostShaped"/> -- which <see cref="PrivonEntryPoint.Run"/> must never fall
/// through to the tray runner for (Gate 031F6H R3).
/// </summary>
public enum NativeMessagingInvocationKind
{
    Normal,
    Host,
    RejectedHostShaped,
}
