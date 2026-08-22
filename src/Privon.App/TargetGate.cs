using Privon.Windows;

namespace Privon.App;

/// <summary>
/// Phase 3B STEP2 -- APP_TARGET_GATE: the ONLY place in this codebase that compares a foreground
/// process name against "ChatGPT." <c>Privon.Windows</c> never knows this name -- it only ever
/// mechanically compares an already-known expected (PID, process name) pair for equality (see
/// <c>ClipboardChangeMonitor</c>'s own <c>CheckForegroundTarget</c>). This type is where the 0.1
/// product decision "which app is PRIVON allowed to protect" actually lives.
///
/// A pure static function, deliberately not a class/interface/plugin abstraction -- 0.1 supports
/// exactly one target, and a pure function over a plain data record needs no fake/mock to test
/// (matches this codebase's established "explicit comparison, no reflection/plugin machinery"
/// minimalism -- see e.g. <c>Privon.Detection.PiiTypeIdCodec</c>, <c>AliasLabelProvider</c>).
///
/// <see cref="StringComparison.OrdinalIgnoreCase"/> deliberately matches the exact comparison
/// <c>ClipboardChangeMonitor.CheckForegroundTarget</c> already uses internally -- keeping this
/// policy-level judgment and Windows's own mechanical re-verification aligned on the same
/// case-sensitivity convention avoids confusing App-approves/Windows-rejects (or vice versa)
/// mismatches for a target that only differs by letter casing.
///
/// TARGET_PROCESS_NAME_SPOOFING remains an accepted, deferred 0.1 risk (Phase 3A.5 STEP1) --
/// unaffected by this type, which only ever compares whatever process name the OS reports.
/// </summary>
internal static class TargetGate
{
    private const string SupportedProcessName = "ChatGPT";

    /// <summary>
    /// True only when <paramref name="snapshot"/> is resolved AND its process name equals
    /// "ChatGPT" (ordinal, case-insensitive). An unresolved snapshot is never treated as eligible
    /// -- see <see cref="ForegroundTargetSnapshot.IsResolved"/>'s own fail-closed contract.
    /// </summary>
    public static bool IsSupportedTarget(ForegroundTargetSnapshot snapshot) =>
        snapshot.IsResolved && string.Equals(snapshot.ProcessName, SupportedProcessName, StringComparison.OrdinalIgnoreCase);
}
