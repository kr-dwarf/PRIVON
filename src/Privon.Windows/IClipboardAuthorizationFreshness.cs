namespace Privon.Windows;

/// <summary>
/// PRIVON 0.3.1 Gate 031F4 -- the single, minimal, opaque synchronous seam a guarded clipboard
/// read/write operation may consult, in addition to (never instead of) its own existing
/// <c>CheckForegroundTarget</c> re-verification, immediately before crossing into native clipboard
/// I/O. This assembly knows nothing about WHY an answer is false -- no Web evidence, no channel,
/// no revision, no foreground epoch, no supported product/origin, no Native Messaging concept of
/// any kind exists on this interface or anywhere else in <c>Privon.Windows</c>. It asks exactly
/// one question: "is the caller's already-authorized state still current, right now?"
///
/// FAIL_CLOSED (Gate 031F4, frozen): a caller-supplied implementation that throws is treated
/// identically to one that returns <see langword="false"/> -- see
/// <see cref="ClipboardChangeMonitor"/>'s own <c>IsAuthorizationStillCurrent</c> helper, the only
/// place this interface is ever invoked. No exception from this method is ever allowed to escape
/// the clipboard owner thread, and none is ever treated as success.
///
/// SYNCHRONOUS_ONLY: deliberately not <c>async</c>/<c>Task</c>-returning -- the guarded clipboard
/// owner thread cannot <see langword="await"/> anything without holding the global clipboard
/// critical section (and, on the write path, the process-wide rollback slot) open across a yield.
/// An implementation must answer immediately from already-held state; it must never perform a
/// browser round-trip, a UI Automation query, or any other blocking/long-running work.
///
/// OPTIONAL_COMPATIBILITY (Gate 031F4 section 13): every guarded entry point that accepts this
/// interface treats <see langword="null"/> as "no additional freshness check" -- reproducing
/// exact pre-0.3.1 Windows behavior. This is the only way existing callers remain unaffected.
/// </summary>
public interface IClipboardAuthorizationFreshness
{
    bool IsStillCurrent();
}
