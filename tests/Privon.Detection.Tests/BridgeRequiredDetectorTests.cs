using Privon.Detection;
using Xunit;

namespace Privon.Detection.Tests;

/// <summary>
/// D3B0-B §25 -- the required-detector precondition, through REAL production seams.
///
/// This is the mechanism that lets a fixture EXECUTE and FAIL for the honest declared reason when
/// a required detector does not yet exist. No NoCandidate substitution, no skip, no fake GREEN.
/// </summary>
public sealed class BridgeRequiredDetectorTests
{
    private static DetectorRegistry NineDetectorRegistry() => DetectorRegistry.Create(
    [
        new Detectors.SecretDetector(), new Detectors.CardNumberDetector(),
        new Detectors.ResidentRegistrationNumberDetector(), new Detectors.BankAccountNumberDetector(),
        new Detectors.EmailDetector(), new Detectors.IpAddressDetector(),
        new Detectors.MacAddressDetector(), new Detectors.GpsCoordinateDetector(),
        new Detectors.PhoneDetector(),
    ]);

    // -----------------------------------------------------------------------------------------
    // §26 rows 16b and 17 -- DEFERRED / NOT CURRENTLY EXECUTABLE. Deliberately no test here.
    //
    // Three tests used to live in this file:
    //     RegisteredButNotReady_FailsWithTheSAMEFrozenIdentity
    //     MissingAndNotReady_AreDistinctObservableStates
    //     MixedMissingAndNotReady_StillSelectsByIdAlone
    //
    // Each built a synthetic "test.snapshot_dependent" detector at rank 900101 and REGISTERED it in
    // a real DetectorRegistry to manufacture a NotReady registration. The independent audit found
    // that invalid: §24.3 A already required that test doubles are "never registered in a
    // DetectorRegistry", and FROZEN_ID_RANK_MAPPING_IS_TOTAL_AND_CLOSED (§16.3.1) now makes such an
    // identity UNREGISTRABLE -- DetectorRegistry.Create rejects it with UnknownDetectorIdException,
    // so those three tests are not merely disallowed, they are impossible to write honestly.
    //
    // They are REMOVED rather than skipped. NOT_READY_PROOF_ACTIVATION_TRIGGER (§16.3.3) states
    // that a deferred obligation "MUST NOT be counted as current GREEN evidence, as a satisfied
    // expectation, or as a passing test", and this repository has no skip convention that would
    // keep a deferred obligation visibly outside the GREEN count.
    //
    // The obligation is ARMED, not cancelled. It activates the moment a candidate production
    // detector whose normative design declares an authority-snapshot dependency is SUBMITTED FOR
    // ACCEPTANCE REVIEW (ACTIVATION_PRECEDES_ACCEPTANCE -- submission, not acceptance). At that
    // point rows 16b and 17 MUST execute against that candidate's REAL production registration and
    // seam, and MUST pass before it may be accepted. They may never be satisfied by a fake
    // detector, a double, a shell, an identity-only registration, or a synthetic registry entry.
    //
    // What is NOT deferred and is still proven below: row 16a (Missing), the frozen failure
    // identity, and the total UTF-8 byte-order selector. What is NOT weakened anywhere in
    // production: Missing != NotReady remains a distinct, separately observable pair of states,
    // DetectorRegistration.Readiness is still queried at use and never cached, and a legitimate
    // future snapshot-dependent detector can still report UnavailableAuthoritySnapshot.
    // See BridgeAuthoritySnapshotTests for the §20.1 loader states, which ARE executable today.

    [Fact]
    public void AllRequiredRegisteredAndReady_Succeeds()
    {
        var registry = NineDetectorRegistry();
        var required = new[] { DetectorId.Create("kr.phone"), DetectorId.Create("global.email") };

        RequiredDetectorPrecondition.Assert(required, registry);      // must not throw
        Assert.True(RequiredDetectorPrecondition.TryAssert(required, registry, out var failure));
        Assert.Null(failure);
    }

    [Fact]
    public void MissingDetector_FailsWithTheAcceptedFrozenIdentity()
    {
        // §26 row 16a -- CURRENTLY EXECUTABLE, and executed here through entirely real seams:
        // a real production registry built from the real nine detectors, and "us.ssn", a genuine
        // FROZEN D2.2 identity (rank 70) that has no implementation and is therefore absent.
        // Nothing synthetic is registered to produce this state -- the state is simply true.
        var registry = NineDetectorRegistry();
        var required = new[] { DetectorId.Create("us.ssn") };

        // Missing means NO registration at all -- observable on the registry itself.
        Assert.False(registry.TryGet(DetectorId.Create("us.ssn"), out _));

        var ex = Assert.Throws<RequiredDetectorNotRegisteredException>(
            () => RequiredDetectorPrecondition.Assert(required, registry));

        Assert.Equal("us.ssn", ex.Id.Value);
        Assert.Equal("RequiredDetectorNotRegistered(us.ssn)", ex.Message);
        Assert.Equal(RequiredDetectorReason.Unregistered, ex.Reason);

        // UNKNOWN_ID_IS_NOT_NO_CANDIDATE -- never a skip, never a pass, never NoCandidate.
        Assert.False(RequiredDetectorPrecondition.TryAssert(required, registry, out var failure));
        Assert.Equal(ex.Id, failure.Id);
        Assert.Equal(ex.Reason, failure.Reason);
    }

    [Fact]
    public void MissingAndNotReady_RemainDistinctProductionSemantics()
    {
        // Row 16b's OBSERVABILITY is deferred; its SEMANTICS are not. This proves the production
        // types still express both states distinctly without registering anything synthetic.
        var registry = NineDetectorRegistry();

        // Missing -- no registration exists for a frozen-but-unimplemented identity.
        Assert.False(registry.TryGet(DetectorId.Create("us.ssn"), out _));
        Assert.Equal(RequiredDetectorReason.Unregistered,
            Assert.Throws<RequiredDetectorNotRegisteredException>(
                () => RequiredDetectorPrecondition.Assert(
                    [DetectorId.Create("us.ssn")], registry)).Reason);

        // NotReady -- a DIFFERENT reason, still declared, so no future change can collapse one
        // into the other unnoticed.
        Assert.NotEqual(RequiredDetectorReason.Unregistered, RequiredDetectorReason.NotReady);

        // Readiness is QUERIED AT USE, never snapshotted at registration (§16.2).
        Assert.True(registry.TryGet(DetectorId.Create("kr.phone"), out var phone));
        Assert.Equal(phone.Detector.Readiness, phone.Readiness);
        Assert.Equal(DetectorReadinessState.Ready, phone.Readiness);

        // NOT_READY_IS_NOT_A_SHELL -- every registration refers to a genuine detector.
        foreach (var registration in registry.All) Assert.NotNull(registration.Detector);

        // The readiness domain is closed at exactly two members, so a legitimate future
        // snapshot-dependent detector still has a state to report.
        Assert.Equal(2, Enum.GetValues<DetectorReadinessState>().Length);
        Assert.Contains(DetectorReadinessState.UnavailableAuthoritySnapshot,
                        Enum.GetValues<DetectorReadinessState>());
    }

    [Fact]
    public void MultipleFailures_SelectTheUtf8ByteLexicographicMinimumId()
    {
        var registry = NineDetectorRegistry();
        var required = new[]
        {
            DetectorId.Create("us.ssn"),
            DetectorId.Create("us.atin"),
            DetectorId.Create("us.itin"),
        };

        var ex = Assert.Throws<RequiredDetectorNotRegisteredException>(
            () => RequiredDetectorPrecondition.Assert(required, registry));

        // "us.atin" < "us.itin" < "us.ssn" byte-lexicographically.
        Assert.Equal("us.atin", ex.Id.Value);
    }

    [Fact]
    public void SelectedId_IsIdenticalUnderEveryInputPermutation()
    {
        var registry = NineDetectorRegistry();
        var ids = new[] { "us.ssn", "us.atin", "us.itin", "us.mbi" }.Select(DetectorId.Create).ToArray();

        foreach (var permutation in Permutations(ids))
        {
            Assert.False(RequiredDetectorPrecondition.TryAssert(permutation, registry, out var failure));
            Assert.Equal("us.atin", failure.Id.Value);
        }
    }

    [Fact]
    public void AssertAndTryAssert_HaveIdenticalSelectionSemantics()
    {
        var registry = NineDetectorRegistry();
        var required = new[] { DetectorId.Create("us.ssn"), DetectorId.Create("us.atin") };

        var thrown = Assert.Throws<RequiredDetectorNotRegisteredException>(
            () => RequiredDetectorPrecondition.Assert(required, registry));
        Assert.False(RequiredDetectorPrecondition.TryAssert(required, registry, out var failure));

        Assert.Equal(thrown.Id, failure.Id);
        Assert.Equal(thrown.Reason, failure.Reason);
    }

    [Fact]
    public void FailureIsNotAResolverOutcome()
    {
        // Placement: a fixture/harness precondition failure, OUTSIDE NORMATIVE_RESOLVER_OUTCOME.
        var ex = Assert.Throws<RequiredDetectorNotRegisteredException>(
            () => RequiredDetectorPrecondition.Assert([DetectorId.Create("us.ssn")], NineDetectorRegistry()));

        Assert.IsNotType<InvalidCandidateSpanException>(ex);
        Assert.IsNotType<UnknownCandidateSemanticFieldException>(ex);
        Assert.IsNotType<InvalidNormativeValueDomainException>(ex);
        Assert.IsNotType<UnresolvableCandidateTieException>(ex);
    }

    private static IEnumerable<DetectorId[]> Permutations(DetectorId[] items)
    {
        if (items.Length <= 1) { yield return items; yield break; }
        for (int i = 0; i < items.Length; i++)
        {
            var rest = items.Take(i).Concat(items.Skip(i + 1)).ToArray();
            foreach (var tail in Permutations(rest))
                yield return new[] { items[i] }.Concat(tail).ToArray();
        }
    }
}
