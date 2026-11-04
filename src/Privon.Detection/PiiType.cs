namespace Privon.Detection;

/// <summary>
/// Taxonomy of detectable PII categories. <see cref="Phone"/> (Phase 2A),
/// <see cref="Email"/> (Phase 2B), <see cref="ResidentRegistrationNumber"/> (Phase 2C),
/// <see cref="CardNumber"/> (Phase 2D), <see cref="BankAccountNumber"/> (Phase 2E),
/// <see cref="Secret"/> (Phase 2F), <see cref="IpAddress"/> (Phase 2K),
/// <see cref="MacAddress"/> (Phase 2L), and <see cref="GpsCoordinate"/> (Phase 2M) have
/// working detectors; other values are added as their detectors are implemented in later
/// phases.
/// </summary>
/// <remarks>
/// D3B0-A A.9 §4.1 -- PII_TYPE_APPEND_ONLY / PII_TYPE_NO_EXPLICIT_VALUES.
///
/// The twelve members after <see cref="GpsCoordinate"/> are APPENDED by the D3B0 bridge and take
/// ordinals 9..20; ordinals 0..8 are left fixed. Never reorder, rename, remove, or insert. No
/// explicit values are declared, and no numeric PiiType is persisted anywhere --
/// <see cref="PiiTypeIdCodec"/> persists a hand-written stable ID STRING, which is why appending
/// is persistence-safe.
///
/// SEMANTIC_PRECEDENCE_NEVER_FROM_ORDINAL: no arbitration, serialization, or precedence decision
/// reads this enum's ordinal. LegacyOwnershipOrder is a literal table (§9.3) and CCSK field 2
/// uses the stable ID string (§12.1).
///
/// A type becoming REPRESENTABLE here does not make any detector exist: the twelve appended
/// members have no detector, no registration, and no activation in D3B0
/// (NO_DETECTOR_ACTIVATION_BY_BRIDGE, §16.6).
/// </remarks>
public enum PiiType
{
    Phone,
    Email,
    ResidentRegistrationNumber,
    CardNumber,
    BankAccountNumber,
    Secret,
    IpAddress,
    MacAddress,
    GpsCoordinate,

    // ---- appended by D3B0 (§4.2); ordinals 9..20 ----
    Name,
    Address,
    UsSsn,
    UsItin,
    UsAtin,
    UsMbi,
    UsPassport,
    UsANumber,
    UsI94,
    UsSevis,
    UsDriverStateId,
    BankAccount,
}
