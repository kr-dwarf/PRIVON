using Privon.Core;

namespace Privon.Detection;

/// <summary>
/// D3B0-A A.9 §12.1 / §13 -- the FROZEN POSITIONS used for CCSK ordering.
///
/// CLOSED_ENUM_VALUE_DOMAIN forbids ordering a normative field "by underlying integer". These
/// literal tables are therefore the only ordering source: they are independent of each enum's
/// C# underlying value, so a future member or an explicit-value edit in Privon.Core cannot move
/// a CCSK position. <c>RiskLevel</c> and <c>DetectionConfidence</c> are pre-existing Privon.Core
/// types whose declarations this bridge deliberately does NOT touch.
///
/// Each method also serves as the §11.2 check-2 closed-domain test: an undefined value has no
/// position and is rejected rather than defaulted.
/// </summary>
internal static class FrozenPositions
{
    public static bool TryRiskLevel(RiskLevel value, out int position)
    {
        switch (value)
        {
            case RiskLevel.Level1: position = 1; return true;
            case RiskLevel.Level2: position = 2; return true;
            case RiskLevel.Level3: position = 3; return true;
            default: position = 0; return false;
        }
    }

    public static bool TryConfidence(DetectionConfidence value, out int position)
    {
        switch (value)
        {
            case DetectionConfidence.Low: position = 1; return true;
            case DetectionConfidence.Medium: position = 2; return true;
            case DetectionConfidence.High: position = 3; return true;
            default: position = 0; return false;
        }
    }

    // Positions 0-6 reproduce the D2.2 lattice; position 7 (NotModelled) is serialization-only
    // and confers NO arbitration ordering (EVIDENCE_NEVER_READ_FOR_LEGACY, §9.2).
    public static bool TryEvidence(ResolutionEvidence value, out int position)
    {
        switch (value)
        {
            case ResolutionEvidence.JOrP: position = 0; return true;
            case ResolutionEvidence.C: position = 1; return true;
            case ResolutionEvidence.S: position = 2; return true;
            case ResolutionEvidence.CJP: position = 3; return true;
            case ResolutionEvidence.SJP: position = 4; return true;
            case ResolutionEvidence.SC: position = 5; return true;
            case ResolutionEvidence.SCJP: position = 6; return true;
            case ResolutionEvidence.NotModelled: position = 7; return true;
            default: position = 0; return false;
        }
    }

    public static bool TryOwnerClaim(OwnerClaim value, out int position)
    {
        switch (value)
        {
            case OwnerClaim.NotApplicable: position = 0; return true;
            case OwnerClaim.False: position = 1; return true;
            case OwnerClaim.True: position = 2; return true;
            default: position = 0; return false;
        }
    }

    /// <summary>
    /// §9.3 LegacyOwnershipOrder -- a LITERAL frozen table. Never read from the PiiType enum, any
    /// class name, IDetector.Name, or registration order. CLOSED: a new semantic type is never
    /// added; a candidate whose type is absent is by definition not LegacyDomain.
    /// </summary>
    public static bool TryLegacyOwnershipOrder(PiiType type, out int order)
    {
        switch (type)
        {
            case PiiType.Phone: order = 1; return true;
            case PiiType.Email: order = 2; return true;
            case PiiType.ResidentRegistrationNumber: order = 3; return true;
            case PiiType.CardNumber: order = 4; return true;
            case PiiType.BankAccountNumber: order = 5; return true;
            case PiiType.Secret: order = 6; return true;
            case PiiType.IpAddress: order = 7; return true;
            case PiiType.MacAddress: order = 8; return true;
            case PiiType.GpsCoordinate: order = 9; return true;
            default: order = 0; return false;
        }
    }

    /// <summary>
    /// §9 ARBITRATION_DOMAIN -- the nine frozen 0.3.2 detector IDs. Domain is a property of
    /// EMISSION: a candidate is LegacyDomain iff it was emitted by one of these, which is
    /// observed through the DetectorId the detector stamped, never inferred from field contents.
    /// </summary>
    public static bool IsLegacyDetectorId(DetectorId id) => id.HasValue && id.Value switch
    {
        "global.secret" or "global.card" or "kr.rrn" or "kr.bank_account" or "global.email"
            or "global.ip" or "global.mac" or "global.gps" or "kr.phone" => true,
        _ => false,
    };
}
