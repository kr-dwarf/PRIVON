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
/// BUG-004 Gate 2G (S2 CONFIRMED defect, closed): TARGET_PROCESS_NAME_SPOOFING is no longer an
/// accepted/deferred risk -- process name alone was proven, this session, to be insufficient
/// (a real, legitimate, differently-branded OpenAI package presenting the identical process name
/// was locally verified to pass the old predicate). This type now ALSO requires the mechanical
/// Windows package-identity fact <see cref="Privon.Windows.ForegroundTargetSnapshot.PackageFamilyName"/>
/// (reported by <c>Privon.Windows</c>, never interpreted there -- see PRODUCT_IDENTITY_POLICY_OWNERSHIP
/// below) to exactly equal the ONE approved current-product identity. Process name remains a cheap,
/// necessary pre-filter -- it is simply no longer sufficient on its own.
///
/// PRODUCT_IDENTITY_POLICY_OWNERSHIP (frozen for 0.2.1): <see cref="SupportedPackageFamilyName"/> is
/// the supported-product policy decision itself -- it exists ONLY here, in <c>Privon.App</c>, never
/// in <c>Privon.Windows</c> (see <c>PrivonWindowsAssembly_HasNoChatGptOrTargetGateNaming</c>'s own
/// enforcing regression, unaffected by this change). <c>Privon.Windows</c> continues to report only
/// the mechanical fact "what package (if any) does this process belong to" -- it has no notion of
/// which identity PRIVON has chosen to trust.
///
/// SUPPORTED_IDENTITY_0_2_1 (Gate 2E/2E.1, locally + Microsoft-Store-catalog verified, not
/// guessed): PackageFamilyName <c>OpenAI.Codex_2p2nqsd0c76g0</c> is the CURRENT/latest official
/// ChatGPT Windows Desktop application's real, live-verified package identity -- despite its own
/// package `Name` still reading "Codex" (a branding/metadata artifact, see Gate 2E.1's own evidence
/// chain). ChatGPT Classic (<c>OpenAI.ChatGPT-Desktop_2p2nqsd0c76g0</c>), every other OpenAI-owned
/// package, every unpackaged same-name executable, and every unresolved/unknown identity state are
/// all deliberately, exhaustively UNSUPPORTED -- there is no fallback path of any kind back to
/// process-name-only authorization when package-identity evidence is anything other than an exact
/// match. PFN_COMPARISON: <see cref="StringComparison.Ordinal"/>, deliberately -- never trimmed,
/// normalized, case-folded, prefix/substring/wildcard-matched, or compared against
/// <c>PackageFullName</c>/version/install path (none of which this type, or the fact model it
/// reads, is even capable of expressing -- see <c>ForegroundTargetSnapshot</c>'s own doc).
/// </summary>
internal static class TargetGate
{
    private const string SupportedProcessName = "ChatGPT";

    /// <summary>See this type's own SUPPORTED_IDENTITY_0_2_1 doc above.</summary>
    private const string SupportedPackageFamilyName = "OpenAI.Codex_2p2nqsd0c76g0";

    /// <summary>
    /// True only when ALL of: <paramref name="snapshot"/> is resolved; its process name equals
    /// "ChatGPT" (ordinal, case-insensitive -- unchanged pre-filter); its package identity was
    /// definitively resolved (<see cref="PackageIdentityResolution.Resolved"/> --
    /// <see cref="PackageIdentityResolution.NoPackage"/>/<see cref="PackageIdentityResolution.Unresolved"/>/
    /// any other value all fail closed here, never treated as "close enough"); and its package
    /// family name exactly (ordinal) equals <see cref="SupportedPackageFamilyName"/>. A
    /// <see langword="null"/> <see cref="ForegroundTargetSnapshot.PackageFamilyName"/>
    /// despite a <c>Resolved</c> state (a shape this codebase's own fact-producing layer should
    /// never emit, but which this policy layer does not trust blindly either) safely evaluates to
    /// <see langword="false"/> via the same ordinal string comparison -- no separate null check is
    /// needed or added. An unresolved snapshot is never treated as eligible -- see
    /// <see cref="ForegroundTargetSnapshot.IsResolved"/>'s own fail-closed contract.
    /// </summary>
    public static bool IsSupportedTarget(ForegroundTargetSnapshot snapshot) =>
        snapshot.IsResolved
        && string.Equals(snapshot.ProcessName, SupportedProcessName, StringComparison.OrdinalIgnoreCase)
        && snapshot.PackageIdentity == PackageIdentityResolution.Resolved
        && string.Equals(snapshot.PackageFamilyName, SupportedPackageFamilyName, StringComparison.Ordinal);
}
