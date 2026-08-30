namespace Privon.Windows;

/// <summary>
/// PRIVON 0.3.0 Gate 1B -- the mechanical, product-policy-free tri-...four-state result of
/// inspecting a process's executable Authenticode signature. Deliberately mirrors
/// <see cref="PackageIdentityResolution"/>'s own established discipline: four structurally distinct
/// facts that must never be conflated with one another, so a future policy layer (TargetGate) can
/// always tell "we never looked" apart from "we looked and it was inconclusive" apart from "we
/// looked and it is definitively NOT trustworthy" apart from "we looked and it IS trustworthy".
/// This type carries NO opinion about which publisher/organization is "supported," "Anthropic," or
/// anything else -- it only ever answers "does this executable carry a verifiable Authenticode
/// signature that chains to a trusted root under the Code Signing EKU."
///
/// Declared with <see cref="NotInspected"/> first (value 0), matching this codebase's "safe value
/// first" discipline (e.g. <c>PackageIdentityResolution.Unresolved</c>,
/// <c>ClipboardReadOutcome.NotRunning</c>): a caller that forgets to set this field, or receives a
/// default-initialized <see cref="ForegroundTargetSnapshot"/>, or a snapshot for a PACKAGED process
/// (where signature inspection is never even attempted -- see
/// <see cref="PackageIdentityResolution.Resolved"/>), never mistakes silence for either "confirmed
/// untrusted" or "confirmed trusted". <see cref="NotInspected"/> is distinct from
/// <see cref="Unresolved"/> for exactly this reason: "never looked" (the ordinary, expected state
/// for every packaged target) must never be conflated with "looked and could not tell" (an
/// inspection failure for an unpackaged target).
/// </summary>
public enum ExecutableSignatureResolution
{
    /// <summary>Signature inspection was never attempted -- the ordinary, expected state whenever
    /// <see cref="ForegroundTargetSnapshot.PackageIdentity"/> is anything other than
    /// <see cref="PackageIdentityResolution.NoPackage"/> (inspection only ever runs for a
    /// definitively unpackaged process). Never treated as equivalent to <see cref="Unresolved"/>.</summary>
    NotInspected,

    /// <summary>Inspection was attempted but did not produce a definitive result (a Windows trust-API
    /// error, the executable becoming unavailable mid-inspection, or any other ordinary,
    /// non-crashing inconclusive condition). Never treated as equivalent to <see cref="NotInspected"/>
    /// or <see cref="Untrusted"/>.</summary>
    Unresolved,

    /// <summary>Inspection succeeded and definitively established that this executable's signature
    /// does NOT meet the trusted-publisher bar (unsigned, self-signed, untrusted chain, bad digest,
    /// revoked, expired with no valid timestamp, missing the Code Signing EKU, or an empty/missing
    /// Organization subject attribute).</summary>
    Untrusted,

    /// <summary>Inspection succeeded and definitively established a trusted, chain-validated
    /// Authenticode signature bearing the Code Signing EKU with a non-empty Organization subject
    /// attribute -- see <see cref="ForegroundTargetSnapshot.SignerOrganization"/> for that value.
    /// Carries no opinion about WHICH organization; that comparison is exclusively
    /// <c>Privon.App.TargetGate</c>'s job.</summary>
    Trusted,
}
