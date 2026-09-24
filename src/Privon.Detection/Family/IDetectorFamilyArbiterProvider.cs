using System.Diagnostics.CodeAnalysis;

namespace Privon.Detection.Family;

/// <summary>
/// D3B0-A A.17 §19.1 -- resolves the concrete arbiter for a family, if one exists.
///
/// FAMILY_SAME_ARBITER (§19.3): a caller may supply a different provider, but it always calls the
/// same production <see cref="DetectorFamilyArbitrationBoundary"/>.
/// </summary>
public interface IDetectorFamilyArbiterProvider
{
    /// <summary>
    /// Returns true and a real arbiter when one is resolved. Returns false, with no arbiter, when
    /// none is.
    /// </summary>
    bool TryGetArbiter(string familyName, [MaybeNullWhen(false)] out IDetectorFamilyArbiter arbiter);
}
