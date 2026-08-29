namespace Privon.Windows;

/// <summary>
/// Phase 3A.5 STEP2 -- test seam over the Win32/BCL foreground-window-identity primitives
/// (GetForegroundWindow/GetWindowThreadProcessId/process-name-by-PID lookup), matching this
/// codebase's existing native-seam pattern (<see cref="IClipboardMonitorNative"/>,
/// <see cref="IClipboardTextNative"/>): an interface with no policy of its own, so
/// <see cref="ForegroundTargetInspector"/>'s ordering/failure logic can be exercised
/// deterministically without any real foreground window or process. The only production
/// implementation is <see cref="Win32ForegroundTargetSource"/>.
///
/// No OpenClipboard/GetClipboardData/SetClipboardData/EmptyClipboard-family member exists here,
/// and never will -- this seam only ever inspects foreground-window/process metadata, per this
/// STEP's explicit PRIVACY scope (zero clipboard content APIs called anywhere in this capability).
/// </summary>
internal interface IForegroundTargetSource
{
    /// <summary>
    /// Raw pass-through of GetForegroundWindow(). A return of 0 is a normal, expected outcome
    /// (no window currently has foreground, e.g. the lock screen) -- not a failure to be
    /// interpreted further here.
    /// </summary>
    nint GetForegroundWindow();

    /// <summary>
    /// Raw pass-through of GetWindowThreadProcessId(hwnd, out pid). Returns false for any
    /// ordinary failure to resolve a process ID (invalid HWND, native call failure, or a
    /// resolved-but-zero PID) -- <paramref name="processId"/> is 0 whenever this returns false.
    /// </summary>
    bool TryGetWindowThreadProcessId(nint hwnd, out uint processId);

    /// <summary>
    /// BUG-004 Gate 2H.3 (E+) -- the ONE identity-resolution primitive. Resolves the process name
    /// AND the Windows package-identity fact for <paramref name="expectedProcessId"/> from a SINGLE
    /// native process handle, and -- while that handle is STILL OPEN -- re-confirms that the current
    /// foreground process is still that same pinned process. Returns <see langword="false"/> if any
    /// step fails or the confirmation does not hold.
    ///
    /// SAME_HANDLE_BINDING (Gate 2H.3, corrected): the production implementation MUST open one
    /// native handle FIRST and derive both facts from it. It must NOT use
    /// <c>System.Diagnostics.Process.ProcessName</c> as one half of the pair: that property and
    /// <c>Process.Handle</c> are two INDEPENDENT PID-keyed lookups, so the two facts could straddle
    /// a PID-reuse boundary and describe two different process instances. (A Gate 2F doc comment
    /// claimed the opposite; that claim was empirically disproven -- creating 271 <c>Process</c>
    /// objects and reading <c>ProcessName</c> added 2 handles to the caller, while forcing
    /// <c>.Handle</c> on those same objects added 131 -- and has been removed.)
    ///
    /// FOREGROUND_CONFIRMATION (Gate 2H.3): opening one handle makes the two facts coherent with ONE
    /// process instance, but does not by itself prove that instance is still FOREGROUND -- the PID
    /// could have been reassigned between reading it from the foreground window and opening it.
    /// The confirmation therefore re-reads the current foreground PID while the handle is still open;
    /// because an open handle keeps that process object (and its identifier) alive, a matching PID at
    /// that instant provably refers to the same pinned instance. The window HANDLE is deliberately
    /// NOT compared -- a different window of the same process is acceptable, so HWND never becomes
    /// identity policy.
    ///
    /// FACTS_ONLY: this method knows no product policy. It never compares against any supported
    /// package family name -- that decision belongs exclusively to <c>Privon.App</c>'s TargetGate.
    ///
    /// On <see langword="false"/>: <paramref name="processName"/> is <see langword="null"/>,
    /// <paramref name="packageIdentity"/> is <see cref="PackageIdentityResolution.Unresolved"/>, and
    /// <paramref name="packageFamilyName"/> is <see langword="null"/>. On <see langword="true"/>,
    /// <paramref name="packageIdentity"/> independently reports whether the package lookup succeeded
    /// (<see cref="PackageIdentityResolution.Resolved"/>), definitively found no package
    /// (<see cref="PackageIdentityResolution.NoPackage"/> -- an ordinary, successful fact for an
    /// unpackaged process), or was inconclusive (<see cref="PackageIdentityResolution.Unresolved"/>);
    /// the last two are never conflated.
    /// </summary>
    bool TryResolveConfirmedForegroundIdentity(
        uint expectedProcessId,
        out string? processName,
        out PackageIdentityResolution packageIdentity,
        out string? packageFamilyName);
}
