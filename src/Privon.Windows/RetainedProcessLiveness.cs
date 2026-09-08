namespace Privon.Windows;

/// <summary>
/// PRIVON 0.3.1 Gate 031F6F (E2) -- the mechanical tri-state liveness fact
/// <see cref="RetainedProcess.CheckLiveness"/> reports for its own retained handle, via a single
/// zero-timeout wait against that handle -- never a PID reopen, a timer, or a polling loop.
/// Declared with <see cref="Unavailable"/> first (value 0), matching this codebase's own "safe value
/// first" discipline: a caller that forgets to check this value, or observes an inconclusive/failed
/// wait, never mistakes silence for "confirmed alive".
/// </summary>
public enum RetainedProcessLiveness
{
    /// <summary>The wait failed, returned an unrecognized result, or the retained handle is already
    /// closed/invalid -- never treated as equivalent to <see cref="Alive"/>.</summary>
    Unavailable = 0,

    /// <summary>The wait reported the process object signaled (WAIT_OBJECT_0-equivalent) -- the
    /// process has terminated.</summary>
    Exited,

    /// <summary>The wait timed out immediately (WAIT_TIMEOUT-equivalent) -- the process is still
    /// running.</summary>
    Alive,
}
