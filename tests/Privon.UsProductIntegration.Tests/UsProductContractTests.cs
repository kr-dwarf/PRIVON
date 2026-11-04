using System.Reflection;
using Privon.Core;
using Privon.Detection;
using Privon.Detection.Detectors;
using Privon.UsProductIntegration;
using static Privon.UsProductIntegration.Tests.UsProductHarness;

namespace Privon.UsProductIntegration.Tests;

// SYNTHETIC DATA ONLY. These tests call the REAL product contracts: IDetector, DetectorRegistry (its frozen D2.2 rank
// table enforces id/rank), DetectionPipeline, DetectionCandidate. Constructing a registry in a test registers nothing in
// the product: it is an in-memory object that dies with the test.
public class UsProductContractTests
{
    public static TheoryData<IDetector, string, PiiType, int> Identities => new()
    {
        { new UsAtinCandidateDetector(), "us.atin", PiiType.UsAtin, 50 },
        { new UsItinCandidateDetector(), "us.itin", PiiType.UsItin, 60 },
        { new UsSsnCandidateDetector(), "us.ssn", PiiType.UsSsn, 70 },
        { new UsMbiCandidateDetector(), "us.mbi", PiiType.UsMbi, 80 },
        { new UsSevisCandidateDetector(), "us.sevis", PiiType.UsSevis, 90 },
    };

    [Theory]
    [MemberData(nameof(Identities))]
    public void EachDetector_StatesItsFrozenIdentityLiterally(IDetector detector, string id, PiiType type, int rank)
    {
        Assert.Equal(id, detector.Id.Value);
        Assert.Equal(type, detector.PiiType);
        Assert.Equal(rank, detector.ResolutionRank);
        Assert.Equal(DetectorReadinessState.Ready, detector.Readiness); // none of the five depends on an authority snapshot
        Assert.False(string.IsNullOrWhiteSpace(detector.Name));
        Assert.True(DetectorId.TryCreate(id, out var parsed));
        Assert.Equal(parsed, detector.Id);
    }

    [Fact]
    public void TheRealRegistry_AcceptsAllFive_ValidatingEveryIdAgainstItsFrozenRank_AndOrdersThemByRank()
    {
        // Each registration is checked by DetectorRegistry against its own frozen id->rank table (rules 1-5).
        var registry = DetectorRegistry.Create(AllFive().Reverse().ToList());

        Assert.Equal(["us.atin", "us.itin", "us.ssn", "us.mbi", "us.sevis"], registry.All.Select(r => r.Id.Value));
        Assert.Equal([50, 60, 70, 80, 90], registry.All.Select(r => r.ResolutionRank));
        Assert.All(registry.All, r => Assert.Equal(DetectorReadinessState.Ready, r.Readiness));
        Assert.Equal(70, registry.GetRank(DetectorId.Create("us.ssn")));
    }

    [Fact]
    public void DetectorNames_AreNotIdentities_AndDoNotClaimToBeOfficialDetectors()
    {
        foreach (var detector in AllFive())
        {
            Assert.EndsWith("CandidateDetector", detector.Name);
            Assert.DoesNotContain(detector.Name, new[] { "SsnDetector", "ItinDetector", "AtinDetector", "MbiDetector", "SevisDetector" });
        }
    }

    [Fact]
    public void ARegistryBackedPipeline_RunsTheFive_AndFindsEveryType()
    {
        var registry = DetectorRegistry.Create(AllFive());
        var pipeline = new DetectionPipeline(registry);
        const string raw = "ATIN: 912-93-4567\nITIN: 912-70-1234\nSSN: 321-54-9876\nMBI: 1A23C45DE67\nSEVIS: N1234567890";

        var result = pipeline.Detect(raw);

        Assert.Equal(
            [PiiType.UsAtin, PiiType.UsItin, PiiType.UsSsn, PiiType.UsMbi, PiiType.UsSevis],
            result.Candidates.OrderBy(c => c.Span.Start).Select(c => c.PiiType));
        Assert.All(result.Candidates, c => Assert.Equal(RiskLevel.Level3, c.RiskLevel));
    }

    [Fact]
    public void ThePipelineThatPrivonShipsByDefault_IsUnchanged_AndEmitsNoUsCandidate()
    {
        const string raw = "ATIN: 912-93-4567 ITIN: 912-70-1234 SSN: 321-54-9876 MBI: 1A23C45DE67 SEVIS: N1234567890";

        var result = DetectionPipeline.CreateDefault().Detect(raw);

        var usTypes = new[] { PiiType.UsSsn, PiiType.UsItin, PiiType.UsAtin, PiiType.UsMbi, PiiType.UsSevis };
        Assert.DoesNotContain(result.Candidates, c => usTypes.Contains(c.PiiType));
    }

    [Fact]
    public void AMixedPipeline_WithTheLegacyDetectors_ResolvesWithoutError_AndKeepsTheLegacyResultsIntact()
    {
        // The nine detectors DetectionPipeline.CreateDefault() itself builds.
        var legacy = new List<IDetector>
        {
            new PhoneDetector(), new EmailDetector(), new ResidentRegistrationNumberDetector(), new CardNumberDetector(),
            new BankAccountNumberDetector(), new SecretDetector(), new IpAddressDetector(), new MacAddressDetector(),
            new GpsCoordinateDetector(),
        };
        const string raw = "mail a.b@example.com, ATIN: 912-93-4567, SSN: 321-54-9876, MBI: 1A23-C45-DE67";

        var legacyOnly = new DetectionPipeline(legacy).Detect(raw);
        var mixed = new DetectionPipeline([.. legacy, .. AllFive()]).Detect(raw);

        Assert.Contains(mixed.Candidates, c => c.PiiType == PiiType.Email);
        Assert.Equal(
            legacyOnly.Candidates.Where(c => c.PiiType == PiiType.Email).Select(c => c.Span),
            mixed.Candidates.Where(c => c.PiiType == PiiType.Email).Select(c => c.Span));
        Assert.Equal(3, mixed.Candidates.Count(c => c.PiiType is PiiType.UsAtin or PiiType.UsSsn or PiiType.UsMbi));
    }

    [Fact]
    public void Isolation_NoProductAssemblyReferencesTheIntegrationAssembly_AndItReferencesOnlyApprovedAssemblies()
    {
        var integration = typeof(UsSsnCandidateDetector).Assembly;
        var referenced = integration.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.Contains("Privon.Detection", referenced);
        Assert.All(
            referenced.Where(n => n.StartsWith("Privon.", StringComparison.Ordinal)),
            n => Assert.Contains(n, new[] { "Privon.Detection", "Privon.Core", "Privon.Storage", "Privon.UsSsn", "Privon.UsMbiSevis" }));
        Assert.DoesNotContain(typeof(IDetector).Assembly.GetReferencedAssemblies(), a => a.Name == integration.GetName().Name);
        Assert.DoesNotContain(typeof(RiskLevel).Assembly.GetReferencedAssemblies(), a => a.Name == integration.GetName().Name);
    }

    [Fact]
    public void TheIntegrationExposesNoRegistrationOrActivationSurface()
    {
        var members = typeof(UsSsnCandidateDetector).Assembly.GetExportedTypes()
            .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Select(m => m.Name)
            .ToList();

        Assert.DoesNotContain(members, n => n.Contains("Register", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Activate", StringComparison.OrdinalIgnoreCase)
            || n.Contains("CreateDefault", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheFactoryHelper_ReturnsTheElevenAuthorityIndependentDetectors_ButRegistersNothing()
    {
        var detectors = UsIntegrationCandidates.CreateDetectors();

        Assert.Equal(
            ["us.atin", "us.itin", "us.ssn", "us.mbi", "us.sevis", "us.passport", "us.a_number", "us.i94",
             "us.driver_state_id", "us.bank_account", "us.name"],
            detectors.Select(d => d.Id.Value));
        Assert.DoesNotContain(
            DetectionPipeline.CreateDefault().Detect("SSN: 321-54-9876").Candidates, c => c.PiiType == PiiType.UsSsn);
    }
}
