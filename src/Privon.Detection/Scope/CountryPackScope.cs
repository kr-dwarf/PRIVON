namespace Privon.Detection.Scope;

/// <summary>
/// The country packs whose detector ownership the product has an APPROVED mapping for. Closed on purpose: Korean legacy
/// detectors' country ownership is not confirmed in the current contract, so no Korean member exists and those detectors
/// keep their existing, unconditional behaviour. A member is added only together with an approved ownership table.
/// </summary>
public enum CountryPack
{
    UnitedStates = 0,
}

/// <summary>
/// What a trusted settings layer observed about one country pack's setting (D2.1/D2.2: country ON/OFF is independent of the
/// category toggles; only a VALID and AUTHENTICATED explicit OFF may stop a country's detectors).
///
/// The product has no persisted or authenticated country setting yet, so this is an INPUT: the caller states what it could
/// verify. Privon.Detection never reads, stores, authenticates or migrates a setting itself.
///
/// FAIL_TOWARD_PROTECTION: <see cref="AuthenticatedOff"/> is the ONLY value that stops a pack. Every other value, including
/// every uncertain one, runs the pack. An undefined enum value is rejected, never read as OFF.
/// </summary>
public enum CountrySettingObservation
{
    AuthenticatedOn = 0,
    AuthenticatedOff = 1,
    Missing = 2,
    Corrupt = 3,
    Unauthenticated = 4,
    UnknownVersion = 5,
}

/// <summary>
/// One country pack's execution scope for the explicit opt-in <see cref="DetectionPipeline"/>.
///
/// <paramref name="RequiredDetectorIds"/> and <paramref name="RequiredFamilyNames"/> are what this configuration promises to
/// run for the pack WHEN IT IS ACTIVE. If the pack is active and one is missing, not Ready, or has no source, the scan fails;
/// it is never reported as a smaller, complete scan. For a pack that is authenticated-OFF they are not consulted at all.
/// Every required detector must be owned by <paramref name="Pack"/> under the approved ownership table.
/// </summary>
public sealed record CountryPackScope(
    CountryPack Pack,
    CountrySettingObservation Setting,
    IReadOnlyCollection<DetectorId> RequiredDetectorIds,
    IReadOnlyCollection<string> RequiredFamilyNames);
