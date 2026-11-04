using System.Diagnostics.CodeAnalysis;

namespace Privon.Detection.Family;

/// <summary>
/// D3B0-A A.17 §19.1 -- the production provider D3B0 ships. It resolves ZERO arbiters for every
/// family, because D3B0 implements none.
///
/// It is not an arbiter, not a shell and not an identity-only implementation: it supplies nothing
/// and can produce no arbitration outcome, exactly as an IAuthoritySnapshotProvider holding no bytes
/// yields SnapshotMissing. The family name is never resolved against the detector registry, and no
/// attempt DetectorId is ever read here. The concrete arbiter lands at the taxpayer S3 transition.
/// </summary>
public sealed class EmptyDetectorFamilyArbiterProvider : IDetectorFamilyArbiterProvider
{
    public bool TryGetArbiter(string familyName, [MaybeNullWhen(false)] out IDetectorFamilyArbiter arbiter)
    {
        ArgumentNullException.ThrowIfNull(familyName);
        arbiter = null;
        return false;
    }
}
