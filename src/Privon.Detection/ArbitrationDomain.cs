namespace Privon.Detection;

/// <summary>
/// D3B0-A A.9 §9 ARBITRATION_DOMAIN. A candidate is <see cref="LegacyDomain"/> iff emitted by
/// one of the nine frozen 0.3.2 detectors; every other candidate -- including every
/// harness-injected candidate -- is <see cref="ContractDomain"/>. The domain is a property of
/// EMISSION, fixed at construction, never inferred from field contents. A component is
/// <see cref="Mixed"/> when its members are not all of one domain.
/// </summary>
public enum ArbitrationDomain
{
    LegacyDomain = 0,
    ContractDomain = 1,
    Mixed = 2,
}
