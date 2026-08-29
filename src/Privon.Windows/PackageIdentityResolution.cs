namespace Privon.Windows;

/// <summary>
/// BUG-004 Gate 2F -- the mechanical, product-policy-free tri-state result of resolving a process's
/// Windows package (MSIX/AppX) identity. Deliberately NOT a nullable string or an empty string --
/// either would collapse two structurally different facts ("this process genuinely has no package
/// identity" vs. "identity inspection itself failed or was inconclusive") into one ambiguous value,
/// which a future policy layer could not safely tell apart. This type carries NO opinion about
/// which package identity is "supported," "official," "ChatGPT," or anything else -- it only ever
/// answers "could this process's package identity be established, and if so, is there one."
///
/// Declared with <see cref="Unresolved"/> first, matching this codebase's established "safe value
/// first" discipline (e.g. <c>ClipboardReadOutcome.NotRunning</c>, <c>ClipboardAttemptOutcome.Done</c>):
/// a caller that forgets to check this value, or receives a default-initialized
/// <see cref="ForegroundTargetSnapshot"/>, never mistakes silence for either "confirmed unpackaged"
/// or "confirmed a specific package" -- both of which are affirmative facts a future policy layer
/// may reasonably treat differently from "we don't actually know."
/// </summary>
public enum PackageIdentityResolution
{
    /// <summary>Package-identity inspection did not produce a definitive result (the underlying
    /// native call failed, the process handle became unusable mid-inspection, or any other ordinary,
    /// non-crashing inconclusive condition). Never treated as equivalent to <see cref="NoPackage"/>.</summary>
    Unresolved,

    /// <summary>Inspection succeeded and definitively established that this process has NO Windows
    /// package identity (an ordinary, unpackaged Win32 executable).</summary>
    NoPackage,

    /// <summary>Inspection succeeded and definitively established this process's package family
    /// name -- see <see cref="ForegroundTargetSnapshot.PackageFamilyName"/>.</summary>
    Resolved,
}
