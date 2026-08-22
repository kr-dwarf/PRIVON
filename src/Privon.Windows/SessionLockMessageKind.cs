namespace Privon.Windows;

/// <summary>
/// Classification of a message the owner thread's message loop retrieved -- deliberately just
/// enough to route the loop's own decisions (raise the lock notification, exit on shutdown, ignore
/// everything else). Mirrors <see cref="ClipboardMonitorMessageKind"/>'s own exact shape/precedent.
/// <see cref="Other"/> covers every WTS session-change reason code except <see cref="Locked"/>
/// (unlock, logon/logoff, console/remote connect/disconnect, ...) -- 0.1 raises a notification for
/// none of them; see <see cref="SessionLockMonitor"/>'s own class doc for why only
/// <c>WTS_SESSION_LOCK</c> is a 0.1 trigger.
/// </summary>
internal enum SessionLockMessageKind
{
    Locked,
    Shutdown,
    Other,
}
