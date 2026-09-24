using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Privon.Core;
using Privon.Detection;
using Privon.Detection.Family;
using Xunit;

namespace Privon.Detection.Tests;

/// <summary>
/// D3B0-A A.17 §19.1–§19.3 structural tests for the family-arbitration boundary.
///
/// Only behaviour lawful at the bridge baseline is executed. The production provider resolves zero
/// arbiters, and FAMILY_SAME_ARBITER forbids a test-only arbiter or boundary. So:
///   AVAILABLE_BRANCH                    = DEFERRED / NOT CURRENTLY EXECUTABLE
///   NULL_REAL_ARBITER_VIOLATION_BRANCH  = DEFERRED / NOT CURRENTLY EXECUTABLE
/// Neither is counted as GREEN evidence. Both become executable only once a real concrete arbiter
/// exists (taxpayer S3). Their contract is covered here by shape only.
/// </summary>
public class FamilyArbitrationBoundaryTests
{
    private static FamilyCandidateAttempt Attempt(string detectorId, FamilyAttemptOutcome outcome) =>
        new(DetectorId.Create(detectorId), RiskLevel.Level3, ResolutionEvidence.S,
            new RawSpan(0, 11), Canonical: null, outcome, RejectionReason: null);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void ProductionProvider_ReportsArbitrationUnavailable_WithNoResult(int attemptCount)
    {
        var attempts = new List<FamilyCandidateAttempt>();
        string[] ids = ["us.atin", "us.itin", "us.ssn"];
        for (int i = 0; i < attemptCount; i++) attempts.Add(Attempt(ids[i], FamilyAttemptOutcome.Accepted));

        bool arbitrated = DetectorFamilyArbitrationBoundary.TryArbitrate(
            new EmptyDetectorFamilyArbiterProvider(), "taxpayer", attempts,
            out var result, out var observation);

        Assert.False(arbitrated);
        Assert.Null(result);
        Assert.Equal(FamilyArbitrationState.ArbitrationUnavailable, observation.State);
        Assert.Equal("taxpayer", observation.FamilyName);
        Assert.Equal(attemptCount, observation.AttemptCount);
    }

    [Fact]
    public void ProductionProvider_ResolvesZeroArbiters_ForAnyFamily()
    {
        var provider = new EmptyDetectorFamilyArbiterProvider();
        foreach (var family in new[] { "taxpayer", "other", string.Empty })
        {
            Assert.False(provider.TryGetArbiter(family, out var arbiter));
            Assert.Null(arbiter);
        }
    }

    [Fact]
    public void Boundary_RejectsNullArguments()
    {
        var provider = new EmptyDetectorFamilyArbiterProvider();
        var none = Array.Empty<FamilyCandidateAttempt>();
        Assert.Throws<ArgumentNullException>(() =>
            DetectorFamilyArbitrationBoundary.TryArbitrate(null!, "taxpayer", none, out _, out _));
        Assert.Throws<ArgumentNullException>(() =>
            DetectorFamilyArbitrationBoundary.TryArbitrate(provider, null!, none, out _, out _));
        Assert.Throws<ArgumentNullException>(() =>
            DetectorFamilyArbitrationBoundary.TryArbitrate(provider, "taxpayer", null!, out _, out _));
    }

    [Fact]
    public void State_IsClosedAtExactlyTwoMembers()
    {
        Assert.Equal(
            new[] { "ArbitrationAvailable", "ArbitrationUnavailable" },
            Enum.GetNames<FamilyArbitrationState>());
    }

    [Fact]
    public void Observation_HasExactlyFamilyNameStateAndAttemptCount()
    {
        var ctor = typeof(FamilyArbitrationObservation).GetConstructors().Single();
        Assert.Equal(
            new[] { typeof(string), typeof(FamilyArbitrationState), typeof(int) },
            ctor.GetParameters().Select(p => p.ParameterType));
        Assert.Equal(
            new[] { "FamilyName", "State", "AttemptCount" },
            ctor.GetParameters().Select(p => p.Name));
    }

    [Fact]
    public void Boundary_Signature_DeclaresANullableResult()
    {
        var method = typeof(DetectorFamilyArbitrationBoundary)
            .GetMethod(nameof(DetectorFamilyArbitrationBoundary.TryArbitrate))!;
        Assert.Equal(typeof(bool), method.ReturnType);

        var parameters = method.GetParameters();
        Assert.Equal(
            new[] { "provider", "familyName", "attempts", "result", "observation" },
            parameters.Select(p => p.Name));
        Assert.True(parameters[3].IsOut);
        Assert.True(parameters[4].IsOut);

        var nullability = new NullabilityInfoContext();
        Assert.Equal(NullabilityState.Nullable, nullability.Create(parameters[3]).WriteState);
        Assert.Equal(NullabilityState.NotNull, nullability.Create(parameters[4]).WriteState);
    }

    [Fact]
    public void Provider_FalsePathMayYieldNoArbiter()
    {
        var parameter = typeof(IDetectorFamilyArbiterProvider)
            .GetMethod(nameof(IDetectorFamilyArbiterProvider.TryGetArbiter))!
            .GetParameters()[1];
        Assert.True(parameter.IsOut);
        var attribute = parameter.GetCustomAttribute<MaybeNullWhenAttribute>();
        Assert.NotNull(attribute);
        Assert.False(attribute!.ReturnValue);
    }

    [Fact]
    public void NullResultException_IsProductionOwned_AndCarriesExactlyItsTypedData()
    {
        var type = typeof(NullFamilyArbitrationResultException);
        Assert.True(type.IsPublic && type.IsSealed);
        Assert.Equal(typeof(Exception), type.BaseType);

        // Only the production boundary may raise it: no externally reachable constructor.
        Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));

        var declared = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.Equal(
            new[] { ("AttemptCount", typeof(int)), ("FamilyName", typeof(string)) },
            declared.Select(p => (p.Name, p.PropertyType)).OrderBy(x => x.Name, StringComparer.Ordinal));
    }
}
