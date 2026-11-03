using System.Reflection;
using Privon.Detection;
using Privon.UsMbiSevis;

namespace Privon.UsMbiSevis.Tests;

// SYNTHETIC DATA ONLY. Properties of the two modules together and of their isolation from the product.
public class UsMbiSevisCrossTests
{
    private const string Mbi = "1A23C45DE67";
    private const string Sevis = "N1234567890";

    [Fact]
    public void TheTwoShapesAreMutuallyExclusive_NoValueIsBothAnMbiAndASevisId()
    {
        // MBI position 1 must be 1-9; SEVIS must start with N. The two grammars cannot accept the same text.
        var rng = new Random(20260502);
        const string alphabet = "0123456789ABCDEFGHJKMNPQRTUVWXYabcdefghjkmnpqrtuvwxy -";

        for (int i = 0; i < 20_000; i++)
        {
            var chars = new char[rng.Next(8, 16)];
            for (int k = 0; k < chars.Length; k++)
            {
                chars[k] = alphabet[rng.Next(alphabet.Length)];
            }

            if (rng.Next(3) == 0)
            {
                chars[0] = 'N';
            }

            string s = new(chars);
            var mbi = UsMbiScanner.Detect(s).Candidates;
            var sevis = UsSevisScanner.Detect(s).Candidates;

            foreach (var m in mbi)
            {
                foreach (var v in sevis)
                {
                    Assert.False(m.Span.Start < v.Span.End && v.Span.Start < m.Span.End, "overlap");
                }
            }
        }

        Assert.Empty(UsMbiScanner.Detect(Sevis).Candidates);
        Assert.Empty(UsSevisScanner.Detect(Mbi).Candidates);
        Assert.Empty(UsMbiScanner.Detect("N1234567890 N0987654321").Candidates);
        Assert.Empty(UsSevisScanner.Detect("1A23-C45-DE67 9Y99 Y99 YY99").Candidates);
    }

    [Fact]
    public void BothIdentifiersInOneDocument_AreFoundIndependently_WithDisjointSpans()
    {
        string text = $"MBI: {Mbi}\nSEVIS ID: {Sevis}\nother: 1A23 C45 DE67 N0987654321";

        var mbi = UsMbiScanner.Detect(text, UsMbiScanner.DefaultStrongLabels, ["SEVIS ID"]);
        var sevis = UsSevisScanner.Detect(text, UsSevisScanner.DefaultStrongLabels, ["MBI"]);

        Assert.Equal(new[] { "5:11", "46:13" }, mbi.Candidates.Select(c => $"{c.Span.Start}:{c.Span.Length}"));
        Assert.Equal(new[] { "27:11", "60:11" }, sevis.Candidates.Select(c => $"{c.Span.Start}:{c.Span.Length}"));
        Assert.Equal(UsIdEvidence.Structure | UsIdEvidence.Context, mbi.Candidates[0].Evidence);
        Assert.Equal(UsIdEvidence.Structure, mbi.Candidates[1].Evidence);
        Assert.Equal(UsIdEvidence.Structure | UsIdEvidence.Context, sevis.Candidates[0].Evidence);
        Assert.Equal(UsIdEvidence.Structure, sevis.Candidates[1].Evidence);
    }

    [Fact]
    public void AdjacentLabelsOfTheOtherType_DoNotHideALaterField()
    {
        string text = string.Concat(Enumerable.Repeat("SEVIS ", 20_000)) + $"MBI: {Mbi}";

        var result = UsMbiScanner.Detect(text, UsMbiScanner.DefaultStrongLabels, ["SEVIS"]);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(UsIdEvidence.Structure | UsIdEvidence.Context, candidate.Evidence);
    }

    [Fact]
    public void OnlySyntheticFixtureValuesAreUsedByTheseModulesOwnTests_NoRealPersonalData()
    {
        // The two reference values are fixed synthetic strings from the frozen corpus. Guard against drift.
        Assert.Equal("1A23C45DE67", Mbi);
        Assert.Equal("N1234567890", Sevis);
    }

    // ---- isolation ------------------------------------------------------------------------------------------------

    [Fact]
    public void TheModule_IsNotRegisteredAndNotWiredIntoAnyProductPath()
    {
        var assembly = typeof(UsMbiScanner).Assembly;
        var references = assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();

        // The only product assembly referenced is Privon.Detection, for FieldBinder / NormalizedView / RawSpan.
        Assert.Contains("Privon.Detection", references);
        Assert.DoesNotContain(references, n => n is not null && n.StartsWith("Privon.", StringComparison.Ordinal) && n != "Privon.Detection");

        // No type of the module implements a product detector contract.
        foreach (var type in assembly.GetTypes())
        {
            Assert.DoesNotContain(type.GetInterfaces(), i => i.Namespace == "Privon.Detection" && i.Name.StartsWith("IDetector", StringComparison.Ordinal));
        }

        // Nothing in Privon.Detection refers to this module.
        var product = typeof(FieldBinder).Assembly;
        Assert.DoesNotContain(product.GetReferencedAssemblies(), a => a.Name == assembly.GetName().Name);
    }

    [Fact]
    public void TheModule_ExposesNoDetectorIdRankOrRegistrationSurface()
    {
        var publicMembers = typeof(UsMbiScanner).Assembly.GetExportedTypes()
            .SelectMany(t => t.GetMembers(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Select(m => m.Name)
            .ToList();

        Assert.DoesNotContain(publicMembers, n => n.Contains("Register", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Rank", StringComparison.OrdinalIgnoreCase)
            || n.Contains("DetectorId", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BothResultTypes_CarryTheCandidatesOnlyNotice()
    {
        Assert.Contains("CANDIDATES_ONLY", UsMbiResult.Notice);
        Assert.Contains("NOT_A_SECURITY_DETERMINATION", UsMbiResult.Notice);
        Assert.DoesNotContain("EXPERIMENT", UsMbiResult.Notice);
        Assert.Equal(UsMbiResult.Notice, UsSevisResult.Notice);
    }
}
