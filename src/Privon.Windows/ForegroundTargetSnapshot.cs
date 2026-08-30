namespace Privon.Windows;

/// <summary>
/// Phase 3A.5 STEP2 -- a single mechanical fact about the current foreground window/process,
/// nothing more. Deliberately carries no product/privacy policy: no notion of "supported target,"
/// "ChatGPT," "eligible," or "protected" exists anywhere in this type or the rest of
/// <c>Privon.Windows</c> -- that judgment belongs entirely to a future <c>Privon.App</c>-owned
/// TargetGate, which is the only thing that ever compares <see cref="ProcessName"/> against a
/// configured supported-target name.
///
/// <see cref="IsResolved"/> is <c>false</c> for every ordinary, expected condition: no foreground
/// window, an invalid HWND/PID, or a process that exited between capturing its PID and looking up
/// its name (a real, expected race -- never a crash). <see cref="ProcessId"/>/<see cref="ProcessName"/>
/// are only meaningful when <see cref="IsResolved"/> is <c>true</c>; the type's own default value
/// (<c>default(ForegroundTargetSnapshot)</c>) already has <see cref="IsResolved"/> false (bool's
/// own default), <see cref="ProcessId"/> 0, and <see cref="ProcessName"/> null -- so a caller that
/// forgets to check <see cref="IsResolved"/> or receives a default-initialized value can never
/// mistake it for a resolved target.
///
/// <see cref="ProcessName"/> is exactly what <c>System.Diagnostics.Process.ProcessName</c> returns
/// for the resolved PID -- the executable's base name without a path or the ".exe" extension, per
/// that property's own documented BCL semantics. No normalization, no case-folding is applied here
/// (a future TargetGate decides its own comparison semantics).
///
/// BUG-004 Gate 2F/2H.3 -- <see cref="PackageIdentity"/>/<see cref="PackageFamilyName"/> extend
/// this type with the SAME mechanical-fact-only discipline: a Windows package (MSIX/AppX) identity
/// fact, never a policy judgment. All four identity facts are produced together by
/// <see cref="ForegroundIdentityCapture.TryCapture"/>, which derives the process name and the
/// package identity from ONE pinned native process handle and confirms, while that handle is still
/// open, that the pinned process is still the foreground process -- see
/// <see cref="IForegroundTargetSource.TryResolveConfirmedForegroundIdentity"/>'s own doc for the
/// full contract and for why the earlier two-independent-lookups approach was unsound. Deliberately
/// excludes package VERSION, <c>PackageFullName</c>, install path, or any other update-brittle
/// value -- <see cref="PackageFamilyName"/> is the only package-identity fact this type will ever
/// carry, by design (a future TargetGate must never be able to pin a version/path even if it wanted
/// to, because this type structurally cannot express one). Both default to the SAME safe values a
/// default-initialized/unresolved snapshot already has (<see cref="PackageIdentityResolution.Unresolved"/>,
/// <see langword="null"/>) so every pre-Gate-2F construction site (which never mentions these two
/// parameters) keeps compiling and behaving identically.
///
/// PRIVON 0.3.0 Gate 1B -- <see cref="ExecutableSignature"/>/<see cref="SignerOrganization"/> extend
/// this type with the SAME mechanical-fact-only discipline, for the Claude Windows target: an
/// Authenticode signature verification fact, never a policy judgment (never compared against
/// "Anthropic, PBC" anywhere in this assembly -- see <see cref="ExecutableSignatureResolution"/>'s
/// own doc). Signature inspection only ever runs when <see cref="PackageIdentity"/> is definitively
/// <see cref="PackageIdentityResolution.NoPackage"/> -- for every packaged snapshot (and for every
/// unresolved one) these two fields stay at their safe defaults
/// (<see cref="ExecutableSignatureResolution.NotInspected"/>, <see langword="null"/>), which are the
/// SAME safe values a default-initialized/pre-Gate-1B snapshot already has, so every earlier
/// construction site (which never mentions these two parameters) keeps compiling and behaving
/// identically. Deliberately excludes executable path, AUMID, certificate thumbprint/serial/issuer,
/// certificate validity window, CompanyName, and registry publisher -- <see cref="SignerOrganization"/>
/// (the Authenticode subject Organization attribute alone) is the only signer-identity fact this
/// type will ever carry, by design (a future TargetGate must never be able to pin a
/// thumbprint/CA/version even if it wanted to, because this type structurally cannot express one).
/// </summary>
public readonly record struct ForegroundTargetSnapshot(
    bool IsResolved,
    uint ProcessId,
    string? ProcessName,
    PackageIdentityResolution PackageIdentity = PackageIdentityResolution.Unresolved,
    string? PackageFamilyName = null,
    ExecutableSignatureResolution ExecutableSignature = ExecutableSignatureResolution.NotInspected,
    string? SignerOrganization = null);
