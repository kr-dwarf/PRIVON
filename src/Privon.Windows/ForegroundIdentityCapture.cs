namespace Privon.Windows;

/// <summary>
/// BUG-004 Gate 2H.3 (E+) -- the ONE coherent foreground-identity capture in this assembly. Every
/// consumer that needs "what is the current foreground process, mechanically" goes through here:
/// <see cref="ForegroundTargetInspector.Capture"/> (authorization-time capture),
/// <c>ClipboardChangeMonitor.CheckForegroundTarget</c> (guarded clipboard read/write CHECK1+CHECK2),
/// and <c>ComposerTextReader.CheckForegroundTarget</c> (guarded composer read CHECK1+CHECK2).
///
/// SINGLE_SEQUENCE_OWNER (frozen): before this type existed, the E-sequence was composed
/// independently in three places, and the two guards' copies had silently drifted to a weaker
/// PID+ProcessName-only comparison -- which is exactly the BUG004-TOCTOU-001 defect. The sequence is
/// now subtle enough (native handle lifetime, and a foreground re-confirmation that is only
/// meaningful WHILE that handle is still open) that duplicating it is a standing hazard. It is
/// therefore written exactly once, here. This is a consolidation, not a new subsystem: it replaces
/// three compositions with one and owns no state, no lock, and no resource.
///
/// FACTS_ONLY: this type contains no product policy of any kind -- no supported package family
/// name, no "ChatGPT", no notion of "eligible". It answers only "what is the current foreground
/// process, coherently". Deciding whether that identity is the supported product remains
/// exclusively <c>Privon.App</c>'s TargetGate's job, and comparing a captured identity against a
/// previously-authorized expected one is the caller's job.
/// </summary>
internal static class ForegroundIdentityCapture
{
    /// <summary>
    /// CAPTURE_FLOW (E+): GetForegroundWindow -> GetWindowThreadProcessId -> single-handle
    /// confirmed identity resolution (see
    /// <see cref="IForegroundTargetSource.TryResolveConfirmedForegroundIdentity"/>, which opens ONE
    /// native handle, derives both facts from it, and re-confirms the foreground PID while that
    /// handle is still open). Any ordinary failure at any step -- no foreground window, an
    /// unresolved/zero PID, the process having exited, or the foreground having moved to a different
    /// process mid-resolution -- yields <see langword="false"/> and a default
    /// <paramref name="snapshot"/>. Never a stale prior snapshot, never a partially-populated one,
    /// never a guess.
    ///
    /// FACT_CAPTURE_LINEARIZATION_POINT: the foreground re-confirmation performed inside
    /// <see cref="IForegroundTargetSource.TryResolveConfirmedForegroundIdentity"/>, while the pinned
    /// handle is still open. At that instant all four returned facts provably describe one native
    /// process instance that was the foreground process.
    /// </summary>
    public static bool TryCapture(IForegroundTargetSource source, out ForegroundTargetSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(source);
        snapshot = default;

        nint hwnd = source.GetForegroundWindow();
        if (hwnd == 0)
            return false;

        // A defensive processId==0 check here, in addition to a false return, means this type never
        // trusts a zero PID as valid even if a (buggy or future) IForegroundTargetSource
        // implementation ever reported success alongside one -- 0 is never a real process ID.
        if (!source.TryGetWindowThreadProcessId(hwnd, out uint processId) || processId == 0)
            return false;

        if (!source.TryResolveConfirmedForegroundIdentity(
                processId, out string? processName, out var packageIdentity, out string? packageFamilyName))
        {
            return false;
        }

        snapshot = new ForegroundTargetSnapshot(
            IsResolved: true,
            ProcessId: processId,
            ProcessName: processName,
            PackageIdentity: packageIdentity,
            PackageFamilyName: packageFamilyName);
        return true;
    }

    /// <summary>
    /// MECHANICAL_IDENTITY_EQUALITY (frozen): compares a freshly-captured CURRENT identity against a
    /// previously-authorized EXPECTED one, across every fact that participates in an authorization
    /// decision. Used identically by all six guarded-operation check points, so no transport can
    /// ever drift back to a weaker PID+ProcessName-only comparison.
    ///
    /// Comparison semantics, deliberately: <see cref="ForegroundTargetSnapshot.ProcessName"/> uses
    /// <see cref="StringComparison.OrdinalIgnoreCase"/> (unchanged, matching this seam's
    /// long-standing convention and TargetGate's own), while
    /// <see cref="ForegroundTargetSnapshot.PackageFamilyName"/> uses
    /// <see cref="StringComparison.Ordinal"/> -- a package family name is an exact machine
    /// identifier, never case-folded. A <see langword="null"/> package family name on either side
    /// compares unequal to any real one, so no separate null branch is needed.
    ///
    /// FACTS_ONLY: this compares current-vs-expected. It never compares against any supported
    /// product constant -- that constant does not exist anywhere in this assembly.
    /// </summary>
    public static bool Matches(ForegroundTargetSnapshot current, ForegroundTargetSnapshot expected) =>
        current.IsResolved
        && expected.IsResolved
        && current.ProcessId == expected.ProcessId
        && string.Equals(current.ProcessName, expected.ProcessName, StringComparison.OrdinalIgnoreCase)
        && current.PackageIdentity == expected.PackageIdentity
        && string.Equals(current.PackageFamilyName, expected.PackageFamilyName, StringComparison.Ordinal);
}
