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
}
