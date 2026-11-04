using Privon.Detection;
using Xunit;

namespace Privon.Detection.Tests;

/// <summary>
/// D3B0-A A.9 §24.3 B -- the PRESENT proof every migrated nullable-Canonical assertion goes
/// through.
///
/// <see cref="DetectionCandidate.Canonical"/> widened to <c>CanonicalValue?</c> (§7.2). The
/// migration obligation (§24.3 B.5) is that every migrated assertion must evaluate EXACTLY as it
/// did before -- a migration that lets an assertion pass vacuously when the canonical is ABSENT
/// is a DEFECT, not a migration. These helpers therefore ASSERT presence rather than
/// null-forgiving it, so an ABSENT canonical fails the test loudly instead of silently comparing
/// two empty nullables as equal.
///
/// This reconstructs no metadata: it reads the production property the production pipeline
/// populated and asserts on it (TESTABILITY_THROUGH_PRODUCTION_SEAMS_ONLY, §26). There is no
/// test-only DetectionCandidate constructor anywhere.
/// </summary>
internal static class CanonicalAssert
{
    /// <summary>Asserts PRESENT and returns the underlying <see cref="CanonicalValue"/>.</summary>
    public static CanonicalValue Present(DetectionCandidate candidate)
    {
        Assert.NotNull(candidate);
        Assert.True(candidate.Canonical.HasValue,
            "DetectionCandidate.Canonical was ABSENT; every legacy detector must emit a PRESENT canonical.");
        return candidate.Canonical!.Value;
    }

    /// <summary>Asserts PRESENT and returns the canonical text (the pre-migration meaning of <c>.Canonical.Value</c>).</summary>
    public static string Text(DetectionCandidate candidate) => Present(candidate).Value;
}
