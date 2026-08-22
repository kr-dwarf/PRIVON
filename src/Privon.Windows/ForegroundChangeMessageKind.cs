namespace Privon.Windows;

/// <summary>
/// Classification of a message the owner thread's message loop retrieved -- deliberately just enough
/// to route the loop's own decisions (raise the foreground-changed notification, exit on shutdown,
/// ignore everything else). Mirrors <see cref="SessionLockMessageKind"/>'s own exact shape/precedent.
/// <see cref="Other"/> covers any message the owner thread's queue receives that is neither the
/// locally-defined shutdown message nor the locally-defined foreground-changed message (e.g. default
/// window messages the message-only window's own WndProc never customizes) -- silently ignored by the
/// message loop.
/// </summary>
internal enum ForegroundChangeMessageKind
{
    Changed,
    Shutdown,
    Other,
}
