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
    private const string SupportedChatGptProcessName = "ChatGPT";

    /// <summary>See this type's own SUPPORTED_IDENTITY_0_2_1 doc above.</summary>
    private const string SupportedPackageFamilyName = "OpenAI.Codex_2p2nqsd0c76g0";

    /// <summary>
    /// PRIVON 0.3.0 Gate 1B -- CLAUDE_RULE (Windows Multi-AI Authorization Contract Freeze, Gate
    /// 0B/0D, frozen). The second and, for 0.3.0, LAST supported target. Deliberately mirrors
    /// SUPPORTED_IDENTITY_0_2_1's own discipline for an unpackaged, Authenticode-signed application:
    /// process name remains a cheap, necessary, INSUFFICIENT pre-filter (<see cref="SupportedClaudeProcessName"/>);
    /// the actual authority is the mechanical, chain-validated, Code-Signing-EKU-bearing signer
    /// evidence <c>Privon.Windows</c> reports (<see cref="ForegroundTargetSnapshot.ExecutableSignature"/>/
    /// <see cref="ForegroundTargetSnapshot.SignerOrganization"/>) -- never any other certificate or
    /// filesystem identity detail, none of which the fact model this type reads is even capable of
    /// expressing.
    ///
    /// ORGANIZATION_COMPARISON: <see cref="StringComparison.Ordinal"/>, deliberately -- never
    /// trimmed, never case-folded. A certificate subject Organization attribute is an exact
    /// identifier, matching <see cref="SupportedPackageFamilyName"/>'s own PFN_COMPARISON
    /// convention. <see cref="ExecutableSignatureResolution.NotInspected"/>/<see cref="ExecutableSignatureResolution.Unresolved"/>/
    /// <see cref="ExecutableSignatureResolution.Untrusted"/> all fail closed here identically to how
    /// a non-<see cref="PackageIdentityResolution.Resolved"/> package identity fails closed for
    /// ChatGPT above -- only a definitive <see cref="ExecutableSignatureResolution.Trusted"/> result
    /// is ever eligible, and only paired with an exact organization match.
    ///
    /// PACKAGE_GATE: requires <see cref="PackageIdentityResolution.NoPackage"/> exactly -- a
    /// packaged process (however it might otherwise present) can never satisfy this rule, keeping
    /// the ChatGPT and Claude rules structurally mutually exclusive (see <see cref="Match"/>).
    /// </summary>
    private const string SupportedClaudeProcessName = "claude";

    /// <summary>See this type's own CLAUDE_RULE doc above -- the ONE approved current publisher
    /// identity (Windows Multi-AI Authorization Contract Freeze, Gate 0B, real Authenticode
    /// evidence read from the installed Claude Windows application).</summary>
    private const string SupportedClaudeOrganization = "Anthropic, PBC";

    /// <summary>
    /// PRIVON 0.3.0 Gate 1B -- TARGET_RESULT: the typed multi-target authorization operation.
    /// Returns the exact matched <see cref="SupportedTarget"/>, or <see langword="null"/> for
    /// "unsupported" -- never a sentinel enum value (see <see cref="SupportedTarget"/>'s own doc).
    /// The two rules below are evaluated independently and are structurally mutually exclusive
    /// (<see cref="PackageIdentityResolution.Resolved"/> vs <see cref="PackageIdentityResolution.NoPackage"/>
    /// can never both hold for one snapshot), so at most one can ever match. This is the ONLY
    /// operation TargetGate exposes that knows about signer/publisher/PFN policy; it owns no native
    /// process handle, no native file handle, no native trust-verification call of any kind, and no
    /// reuse/cache lifetime -- those are exclusively <c>Privon.Windows</c>'s mechanical concern (see
    /// <see cref="ForegroundTargetSnapshot"/>'s and <c>Win32ForegroundTargetSource</c>'s own docs).
    /// </summary>
    public static SupportedTarget? Match(ForegroundTargetSnapshot snapshot)
    {
        if (IsSupportedChatGptTarget(snapshot))
            return SupportedTarget.ChatGpt;
        if (IsSupportedClaudeTarget(snapshot))
            return SupportedTarget.Claude;
        return null;
    }

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
    ///
    /// PRIVON 0.3.0 Gate 1B: now a thin wrapper over <see cref="Match"/> -- <c>Match(snapshot) is
    /// not null</c> -- so every existing caller (six guarded-operation check points, the retry
    /// controller, the decision-action resolver) keeps compiling and behaving byte-identically
    /// without threading <see cref="SupportedTarget"/> through the clipboard pipeline, which does
    /// not need it in 0.3.0.
    /// </summary>
    public static bool IsSupportedTarget(ForegroundTargetSnapshot snapshot) => Match(snapshot) is not null;

    private static bool IsSupportedChatGptTarget(ForegroundTargetSnapshot snapshot) =>
        snapshot.IsResolved
        && string.Equals(snapshot.ProcessName, SupportedChatGptProcessName, StringComparison.OrdinalIgnoreCase)
        && snapshot.PackageIdentity == PackageIdentityResolution.Resolved
        && string.Equals(snapshot.PackageFamilyName, SupportedPackageFamilyName, StringComparison.Ordinal);

    private static bool IsSupportedClaudeTarget(ForegroundTargetSnapshot snapshot) =>
        snapshot.IsResolved
        && string.Equals(snapshot.ProcessName, SupportedClaudeProcessName, StringComparison.OrdinalIgnoreCase)
        && snapshot.PackageIdentity == PackageIdentityResolution.NoPackage
        && snapshot.ExecutableSignature == ExecutableSignatureResolution.Trusted
        && string.Equals(snapshot.SignerOrganization, SupportedClaudeOrganization, StringComparison.Ordinal);
}
