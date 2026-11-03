using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Privon.Detection;
using Privon.Detection.Authority;
using Privon.Detection.Detectors;
using Privon.Detection.Scope;
using Privon.UsProductIntegration;
using static Privon.UsProductIntegration.Tests.UsProductHarness;

namespace Privon.UsProductIntegration.Tests;

// The production authority artifact: three snapshots built from PUBLIC NANPA and USPS files (read once, read-only, kept verbatim under
// AuthorityData/source) and embedded in the integration assembly. This suite (1) pins the artifact's provenance, (2) re-derives its normalized
// data from the verbatim sources with an INDEPENDENT C# implementation (the artifact itself was written by build_authority.py), (3) loads it
// through the production loader and the production scope check, and (4) proves what happens when any of that is wrong. The four-entry D3A
// fixtures are never production data: the production scope check rejects them.
public class UsAuthorityProductionTests
{
    private const string NpaUrl = "https://reports.nanpa.com/public/npa_report.csv";
    private const string SuffixUrl = "https://pe.usps.com/text/pub28/28apc_002.htm";
    private const string StateUrl = "https://pe.usps.com/text/pub28/28apb.htm";

    private static readonly UsAuthoritySet Production = UsAuthoritySet.Production;

    private static string SourcePath(string file) => Path.Combine(AppContext.BaseDirectory, "AuthorityData", "source", file);

    private static byte[] Bytes(IAuthoritySnapshotProvider provider, AuthoritySnapshotId id)
    {
        Assert.True(provider.TryGetBytes(id, out var bytes));
        return bytes.ToArray();
    }

    private static AuthoritySnapshotMetadata Meta(IAuthoritySnapshotProvider provider, AuthoritySnapshotId id)
    {
        Assert.True(provider.TryGetMetadata(id, out var metadata));
        return metadata;
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    // ===== a provider built from the production bytes, with one deliberate defect ============================================

    private sealed class Derived : IAuthoritySnapshotProvider
    {
        private readonly Dictionary<string, byte[]> _bytes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AuthoritySnapshotMetadata> _metadata = new(StringComparer.Ordinal);

        public Derived()
        {
            var real = new UsProductionAuthorityProvider();
            foreach (var id in new[] { UsAuthoritySet.NpaId, UsAuthoritySet.SuffixId, UsAuthoritySet.StateId })
            {
                _bytes[id.Value] = Bytes(real, id);
                _metadata[id.Value] = Meta(real, id);
            }
        }

        public string Text(AuthoritySnapshotId id) => Encoding.UTF8.GetString(_bytes[id.Value]);

        public Derived Without(AuthoritySnapshotId id) { _bytes.Remove(id.Value); return this; }

        public Derived WithMeta(AuthoritySnapshotId id, Func<AuthoritySnapshotMetadata, AuthoritySnapshotMetadata> change)
        {
            _metadata[id.Value] = change(_metadata[id.Value]);
            return this;
        }

        public Derived FlipOneByte(AuthoritySnapshotId id)
        {
            var copy = (byte[])_bytes[id.Value].Clone();
            copy[0] = copy[0] == (byte)'2' ? (byte)'3' : (byte)'2';
            _bytes[id.Value] = copy;
            return this;
        }

        /// <summary>A SELF-CONSISTENT replacement: bytes, record count and hash all agree, so only the content itself can be wrong.</summary>
        public Derived Replace(AuthoritySnapshotId id, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            _bytes[id.Value] = bytes;
            _metadata[id.Value] = _metadata[id.Value] with
            {
                ExpectedRecordCount = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length,
                ExpectedSha256Hex = Sha(bytes),
            };
            return this;
        }

        public bool TryGetBytes(AuthoritySnapshotId id, out ReadOnlyMemory<byte> bytes)
        {
            bool found = _bytes.TryGetValue(id.Value, out var value);
            bytes = found ? value : default;
            return found;
        }

        public bool TryGetMetadata(AuthoritySnapshotId id, out AuthoritySnapshotMetadata metadata)
        {
            bool found = _metadata.TryGetValue(id.Value, out var value);
            metadata = found ? value! : null!;
            return found;
        }
    }

    private static string WithoutLines(string text, Func<string, bool> drop) =>
        string.Concat(text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => !drop(l)).Select(l => l + "\n"));

    // ===== 1. provenance =====================================================================================================

    [Fact]
    public void TheEmbeddedArtifact_CarriesTheOfficialProvenance_ForEverySnapshot()
    {
        var provider = new UsProductionAuthorityProvider();

        Assert.Equal("us-authority/2026-11-02.1", provider.ArtifactVersion);
        foreach (var (id, url, count, sha) in new[]
                 {
                     (UsAuthoritySet.NpaId, NpaUrl, 378, "d38fee57b62e8f87fbed01965801575856eed3fea7751ef3ffd302411856848c"),
                     (UsAuthoritySet.SuffixId, SuffixUrl, 549, "a4e031653e74de4e7c98acc215107bc6d11f16b527c140df709f8bd2c6d743b0"),
                     (UsAuthoritySet.StateId, StateUrl, 59, "bed45633dff20b979dec923894e19a07c9eb09107cc01c4ccbd9917e1b14926d"),
                 })
        {
            var metadata = Meta(provider, id);
            Assert.Equal(url, metadata.SourceUrl);
            Assert.Equal(new DateOnly(2026, 11, 2), metadata.RetrievalDate);
            Assert.Equal("1", metadata.Version);                 // the loader's schema version; the artifact version is above
            Assert.Equal(count, metadata.ExpectedRecordCount);
            Assert.Equal(sha, metadata.ExpectedSha256Hex);
            Assert.Equal(sha, Sha(Bytes(provider, id)));         // the hash really is the hash of the embedded bytes
            Assert.True(AuthoritySnapshotLoader.TryLoad(provider, id, out var snapshot, out var validation));
            Assert.Equal(AuthoritySnapshotState.SnapshotValid, validation.State);
            Assert.Equal(count, snapshot.NormalizedRecords.Count);
        }

        Assert.Equal("https://nanpa.com/reports/npa-reports", Meta(provider, UsAuthoritySet.NpaId).CatalogUrl);
    }

    [Fact]
    public void TheArtifact_IsNotTheD3Fixture_AndTheProductionScopeCheckRejectsTheFixtures()
    {
        // different data, different size, different hash
        Assert.NotEqual(UsTestAuthorityProvider.NpaSha256, Meta(new UsProductionAuthorityProvider(), UsAuthoritySet.NpaId).ExpectedSha256Hex);

        // the SAME loader path that accepts the real artifact refuses the four-entry fixtures, which are self-consistent but not production scope
        var rejected = UsAuthoritySet.LoadProduction(UsTestAuthorityProvider.Valid());

        Assert.False(rejected.PhoneReady);
        Assert.False(rejected.AddressReady);
        Assert.NotEmpty(rejected.ScopeIssues.Concat(new[] { rejected.NpaState.ToString() }));
        Assert.Contains(rejected.ScopeIssues, i => i.StartsWith("usps.suffix anchor", StringComparison.Ordinal));
        Assert.Contains(rejected.ScopeIssues, i => i.StartsWith("usps.state code", StringComparison.Ordinal));
        Assert.Equal(AuthoritySnapshotState.SnapshotBuildFailureDuplicateOrInvalidNormalizedRecord, rejected.NpaState); // "212" has no jurisdiction
        Assert.Equal(11, UsIntegrationCandidates.ReadinessReport(rejected).Count(r => r.Readiness == DetectorReadinessState.Ready));
        // while the shape-only entry point still accepts them, as the D3A tests need -- and labels the result as not production
        Assert.Equal(UsAuthorityOrigin.Provided, UsAuthoritySet.Load(UsTestAuthorityProvider.Valid()).Origin);
    }

    [Fact]
    public void TheProductionSet_IsLoadedFromTheArtifact_WithTheProductionScope_AndNothingFetchedAtRunTime()
    {
        Assert.Equal(UsAuthorityOrigin.ProductionScope, Production.Origin);
        Assert.Empty(Production.ScopeIssues);
        Assert.Equal(AuthoritySnapshotState.SnapshotValid, Production.NpaState);
        Assert.Equal(AuthoritySnapshotState.SnapshotValid, Production.SuffixState);
        Assert.Equal(AuthoritySnapshotState.SnapshotValid, Production.StateState);

        var assembly = typeof(UsAuthoritySet).Assembly;
        var referenced = assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        Assert.DoesNotContain(referenced, n => n is "System.Net.Http" or "System.Net.Sockets" or "System.Net.Requests" or "System.Net.WebClient");
        Assert.Equal(
            ["Privon.UsProductIntegration.AuthorityData.authority-manifest.json", "Privon.UsProductIntegration.AuthorityData.us-npa.txt",
             "Privon.UsProductIntegration.AuthorityData.usps-state.txt", "Privon.UsProductIntegration.AuthorityData.usps-suffix.txt"],
            assembly.GetManifestResourceNames().Where(n => n.Contains("AuthorityData", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AnAssemblyWithoutTheArtifact_ProvidesNothing_AndNothingIsInvented()
    {
        var provider = new UsProductionAuthorityProvider(typeof(string).Assembly);
        var set = UsAuthoritySet.LoadProduction(provider);

        Assert.Null(provider.ArtifactVersion);
        Assert.False(provider.TryGetBytes(UsAuthoritySet.NpaId, out _));
        Assert.False(set.PhoneReady);
        Assert.False(set.AddressReady);
        Assert.Equal(AuthoritySnapshotState.SnapshotMissing, set.NpaState);
    }

    // ===== 2. the normalized data is exactly what the verbatim sources say ===================================================

    [Fact]
    public void TheVerbatimSources_MatchTheirRecordedHashAndSize()
    {
        var manifest = System.Text.Json.JsonDocument.Parse(
            typeof(UsAuthoritySet).Assembly.GetManifestResourceStream("Privon.UsProductIntegration.AuthorityData.authority-manifest.json")!);

        foreach (var entry in manifest.RootElement.GetProperty("snapshots").EnumerateArray())
        {
            string file = Path.GetFileName(entry.GetProperty("sourceFile").GetString()!);
            byte[] source = File.ReadAllBytes(SourcePath(file));

            Assert.Equal(entry.GetProperty("sourceBytes").GetInt64(), source.Length);
            Assert.Equal(entry.GetProperty("sourceSha256").GetString(), Sha(source));
        }
    }

    private static List<string> ParseCsvRows(string text, out List<List<string>> rows)
    {
        rows = [];
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\r') { }
            else if (c == '\n') { row.Add(field.ToString()); field.Clear(); rows.Add(row); row = []; }
            else field.Append(c);
        }

        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return row;
    }

    private static List<List<string>> HtmlTable(string html, int index)
    {
        var table = Regex.Matches(html, "<table.*?</table>", RegexOptions.Singleline)[index].Value;
        var rows = new List<List<string>>();
        foreach (Match r in Regex.Matches(table, "<tr.*?</tr>", RegexOptions.Singleline))
        {
            rows.Add(Regex.Matches(r.Value, "<t[hd][^>]*>(.*?)</t[hd]>", RegexOptions.Singleline)
                .Select(c => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(c.Groups[1].Value, "<[^>]+>", " ")), @"\s+", " ").Trim())
                .ToList());
        }

        return rows;
    }

    [Fact]
    public void TheNpaSnapshot_IsExactlyTheUsGeographicInServiceCodes_OfTheVerbatimNanpaFile()
    {
        string text = File.ReadAllText(SourcePath("nanpa-npa_report.csv"), Encoding.UTF8).TrimStart('﻿');
        ParseCsvRows(text, out var rows);
        Assert.Equal("File Date", rows[0][0]);
        Assert.Equal("11/02/2026", rows[0][1]);                    // the file's own date: the day it was read
        var header = rows[1];
        int npa = header.IndexOf("NPA_ID"), use = header.IndexOf("USE"), inService = header.IndexOf("IN_SERVICE"),
            location = header.IndexOf("LOCATION"), country = header.IndexOf("COUNTRY");
        var expected = rows.Skip(2).Where(r => r.Count == header.Count && r[country] == "US" && r[use] == "G" && r[inService] == "Y")
            .OrderBy(r => r[npa], StringComparer.Ordinal).Select(r => r[npa] + " " + r[location]).ToList();

        string embedded = Encoding.UTF8.GetString(Bytes(new UsProductionAuthorityProvider(), UsAuthoritySet.NpaId));

        Assert.Equal(378, expected.Count);
        Assert.Equal(string.Concat(expected.Select(l => l + "\n")), embedded);
        // ownership, from the source itself: no Canadian, Caribbean or non-geographic code is in the list
        var all = rows.Skip(2).Where(r => r.Count == header.Count).ToList();
        foreach (string code in new[] { "416", "604", "242", "876", "809", "800", "888", "900", "500", "451", "565", "710" })
        {
            Assert.DoesNotContain(code + " ", embedded);
            Assert.DoesNotContain(all, r => r[npa] == code && r[country] == "US" && r[use] == "G" && r[inService] == "Y");
        }

        foreach (string territory in new[] { "PR", "VI", "GU", "AS", "CNMI", "DC" })
        {
            Assert.Contains(" " + territory + "\n", embedded);
        }
    }

    [Fact]
    public void TheSuffixSnapshot_IsExactlyTheUnionOfTheThreeColumns_OfTheVerbatimUspsAppendixC1()
    {
        var rows = HtmlTable(File.ReadAllText(SourcePath("usps-pub28-appendix-c1-street-suffix.htm"), Encoding.UTF8), 1);
        Assert.Equal("Primary Street Suffix Name", rows[0][0]);
        var tokens = rows.Skip(1).SelectMany(r => r).Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToList();

        string embedded = Encoding.UTF8.GetString(Bytes(new UsProductionAuthorityProvider(), UsAuthoritySet.SuffixId));

        Assert.Equal(549, tokens.Count);
        Assert.All(tokens, t => Assert.Matches("^[A-Z]{2,}$", t));
        Assert.Equal(string.Concat(tokens.Select(t => t + "\n")), embedded);
    }

    [Fact]
    public void TheStateSnapshot_IsExactlyTheAppendixBStatePossessionTable_AndExcludesTheMilitaryTable()
    {
        var rows = HtmlTable(File.ReadAllText(SourcePath("usps-pub28-appendix-b-state-possession.htm"), Encoding.UTF8), 1);
        Assert.Equal(["State/Possession", "Abbreviation"], rows[0]);
        var codes = rows.Skip(1).Select(r => r[1]).OrderBy(c => c, StringComparer.Ordinal).ToList();

        string embedded = Encoding.UTF8.GetString(Bytes(new UsProductionAuthorityProvider(), UsAuthoritySet.StateId));

        Assert.Equal(59, codes.Count);
        Assert.Equal(string.Concat(codes.Select(c => c + "\n")), embedded);
        foreach (string military in new[] { "AA", "AE", "AP" })
        {
            Assert.DoesNotContain(military + "\n", embedded);
        }
    }

    // ===== 3. what the real data makes ready =================================================================================

    [Fact]
    public void WithTheProductionArtifact_AllThirteenDetectorsAreReady_AndTheRequiredListIsAllThirteen()
    {
        var report = UsIntegrationCandidates.ReadinessReport(Production);

        Assert.Equal(13, report.Count);
        Assert.All(report, r => Assert.Equal(DetectorReadinessState.Ready, r.Readiness));
        Assert.Equal(13, UsIntegrationCandidates.CreateDetectors(Production).Count);
        Assert.Equal(13, UsIntegrationCandidates.RequiredDetectorIds(Production).Count);
        Assert.Equal(13, UsOptInPipeline.UsScope(CountrySettingObservation.Missing, Production).RequiredDetectorIds.Count);
    }

    [Fact]
    public void TheProductionArtifact_IsNotPickedUpImplicitly_TheDefaultIsStillElevenOfThirteen()
    {
        Assert.Equal(11, UsIntegrationCandidates.ReadinessReport().Count(r => r.Readiness == DetectorReadinessState.Ready));
        Assert.Equal(11, UsIntegrationCandidates.CreateDetectors().Count);
        Assert.DoesNotContain(UsOptInPipeline.Create().Detect("Phone: 202-555-0147 Address: 123 Main Street").Candidates,
            c => c.PiiType is PiiType.Phone or PiiType.Address);
    }

    [Theory]
    [InlineData("Phone: 202-555-0147")]          // DC
    [InlineData("(415) 555-0100")]               // CA
    [InlineData("+1 (787) 555-0147")]            // Puerto Rico
    [InlineData("Tel: 671.555.0123")]            // Guam
    [InlineData("684 555 0123")]                 // American Samoa
    [InlineData("Phone: 6705550123")]            // Northern Mariana Islands (CNMI)
    [InlineData("340-555-0123")]                 // U.S. Virgin Islands
    [InlineData("1-907-555-0123")]               // Alaska
    [InlineData("+18085550123")]                 // Hawaii
    public void TheRealNpaList_AcceptsStatesAndTerritories(string raw) => Assert.Single(Run(new UsPhoneCandidateDetector(Production), raw));

    [Theory]
    [InlineData("416-555-0123")]                 // Toronto (Canada)
    [InlineData("604-555-0123")]                 // British Columbia
    [InlineData("242-555-0123")]                 // Bahamas
    [InlineData("876-555-0123")]                 // Jamaica
    [InlineData("809-555-0123")]                 // Dominican Republic
    [InlineData("800-555-0123")]                 // toll-free
    [InlineData("888-555-0123")]
    [InlineData("900-555-0123")]                 // premium
    [InlineData("500-555-0123")]                 // personal communication (non-geographic)
    [InlineData("451-555-0123")]                 // assigned to a US state but not in service in the source
    [InlineData("565-555-0123")]                 // planned (in service 2027), not in service yet
    [InlineData("111-555-0123")]                 // invalid leading digit
    [InlineData("Phone: 8005550123")]
    [InlineData("+1 (416) 555-0123")]
    public void TheRealNpaList_RejectsOtherNanpCountriesNonGeographicAndNotInServiceCodes(string raw) =>
        Assert.Empty(Run(new UsPhoneCandidateDetector(Production), raw));

    [Theory]
    [InlineData("123 Main Street, Austin, TX 78701")]       // the full suffix name
    [InlineData("123 Main St, Austin, TX 78701")]
    [InlineData("456 Oak BLVD, Albany, NY 12207")]
    [InlineData("789 Elm Avenue Apt 4, Miami, FL 33101")]
    [InlineData("1 Harbor Dr, Majuro, MH 96960")]            // USPS lists MH in Appendix B
    [InlineData("12 Beach Rd, Hagatna, GU 96910")]
    [InlineData("Address: 5 Pine Ct")]
    public void TheRealUspsLists_RecogniseSuffixesAndStatesAndPossessions(string raw) =>
        Assert.Single(Run(new UsAddressCandidateDetector(Production), raw));

    [Theory]
    [InlineData("123 Main Floobar, Austin, TX 78701")]       // not a USPS suffix
    [InlineData("123 Main St, Austin, ZZ 78701")]            // not a state
    [InlineData("123 Main St, Toronto, ON M5V 3A8")]         // a Canadian province
    [InlineData("PSC 1 Box 2, APO, AE 09001")]               // the military table is not a state / possession
    [InlineData("123 Main St, Austin, tx 78701")]
    [InlineData("Address: 123 Main Floobar")]
    public void TheRealUspsLists_RejectWhatTheyDoNotList(string raw) => Assert.Empty(Run(new UsAddressCandidateDetector(Production), raw));

    // ===== 4. 11 -> 12 -> 13 =================================================================================================

    [Fact]
    public void TheReadyCount_GrowsFromElevenToTwelveToThirteen_AsTheRealSnapshotsAreProvided()
    {
        int Ready(UsAuthoritySet s) => UsIntegrationCandidates.ReadinessReport(s).Count(r => r.Readiness == DetectorReadinessState.Ready);

        Assert.Equal(11, Ready(UsAuthoritySet.Unavailable));
        Assert.Equal(11, Ready(UsAuthoritySet.LoadProduction(new Derived().Without(UsAuthoritySet.NpaId).Without(UsAuthoritySet.SuffixId).Without(UsAuthoritySet.StateId))));
        Assert.Equal(12, Ready(UsAuthoritySet.LoadProduction(new Derived().Without(UsAuthoritySet.SuffixId).Without(UsAuthoritySet.StateId))));   // phone
        Assert.Equal(12, Ready(UsAuthoritySet.LoadProduction(new Derived().Without(UsAuthoritySet.NpaId))));                                      // address
        Assert.Equal(11, Ready(UsAuthoritySet.LoadProduction(new Derived().Without(UsAuthoritySet.NpaId).Without(UsAuthoritySet.StateId))));      // address needs both
        Assert.Equal(11, Ready(UsAuthoritySet.LoadProduction(new Derived().Without(UsAuthoritySet.NpaId).Without(UsAuthoritySet.SuffixId))));
        Assert.Equal(13, Ready(UsAuthoritySet.LoadProduction(new Derived())));
    }

    // ===== 5. every kind of damage makes the depending detector not ready ====================================================

    public static TheoryData<string, string> Damage => new()
    {
        { "us.npa", "missing" }, { "us.npa", "flipped-byte" }, { "us.npa", "no-retrieval-date" }, { "us.npa", "no-url" }, { "us.npa", "version" },
        { "us.npa", "count" }, { "us.npa", "duplicate" }, { "us.npa", "invalid-record" }, { "us.npa", "contradictory-jurisdiction" },
        { "usps.suffix", "missing" }, { "usps.suffix", "flipped-byte" }, { "usps.suffix", "no-retrieval-date" }, { "usps.suffix", "version" },
        { "usps.suffix", "count" }, { "usps.suffix", "duplicate" }, { "usps.suffix", "invalid-record" },
        { "usps.state", "missing" }, { "usps.state", "flipped-byte" }, { "usps.state", "no-retrieval-date" }, { "usps.state", "version" },
        { "usps.state", "count" }, { "usps.state", "duplicate" }, { "usps.state", "invalid-record" },
    };

    private static Derived Damaged(string id, string how)
    {
        var snapshot = id switch { "us.npa" => UsAuthoritySet.NpaId, "usps.suffix" => UsAuthoritySet.SuffixId, _ => UsAuthoritySet.StateId };
        var d = new Derived();
        string text = d.Text(snapshot);
        return how switch
        {
            "missing" => d.Without(snapshot),
            "flipped-byte" => d.FlipOneByte(snapshot),
            "no-retrieval-date" => d.WithMeta(snapshot, m => m with { RetrievalDate = default }),
            "no-url" => d.WithMeta(snapshot, m => m with { SourceUrl = " " }),
            "version" => d.WithMeta(snapshot, m => m with { Version = "2" }),
            "count" => d.WithMeta(snapshot, m => m with { ExpectedRecordCount = m.ExpectedRecordCount + 1 }),
            "duplicate" => d.Replace(snapshot, text + text.Split('\n')[0] + "\n"),
            "invalid-record" => d.Replace(snapshot, text + (id == "us.npa" ? "93X NY\n" : id == "usps.suffix" ? "S!\n" : "N1\n")),
            "contradictory-jurisdiction" => d.Replace(snapshot, text + "212 NJ\n"),
            _ => throw new ArgumentOutOfRangeException(nameof(how)),
        };
    }

    [Theory]
    [MemberData(nameof(Damage))]
    public void AnyDamage_MakesOnlyTheDependingDetectorNotReady_AndTheOtherStaysReady(string id, string how)
    {
        var set = UsAuthoritySet.LoadProduction(Damaged(id, how));

        if (id == "us.npa")
        {
            Assert.False(set.PhoneReady);
            Assert.True(set.AddressReady);
            Assert.Equal(12, UsIntegrationCandidates.ReadinessReport(set).Count(r => r.Readiness == DetectorReadinessState.Ready));
            Assert.Empty(Run(new UsPhoneCandidateDetector(set), "Phone: 202-555-0147 (415) 555-0100 +1 (787) 555-0147"));
        }
        else
        {
            Assert.True(set.PhoneReady);
            Assert.False(set.AddressReady);
            Assert.Equal(12, UsIntegrationCandidates.ReadinessReport(set).Count(r => r.Readiness == DetectorReadinessState.Ready));
            Assert.Empty(Run(new UsAddressCandidateDetector(set), "Address: 123 Main Street\n123 Main St, Austin, TX 78701"));
        }
    }

    [Fact]
    public void TheLoaderStates_AreTheFrozenOnes_ForTheRealData()
    {
        Assert.Equal(AuthoritySnapshotState.SnapshotMissing, UsAuthoritySet.LoadProduction(Damaged("us.npa", "missing")).NpaState);
        Assert.Equal(AuthoritySnapshotState.SnapshotBuildFailureHashMismatch, UsAuthoritySet.LoadProduction(Damaged("us.npa", "flipped-byte")).NpaState);
        Assert.Equal(AuthoritySnapshotState.SnapshotBuildFailureMissingMetadata, UsAuthoritySet.LoadProduction(Damaged("us.npa", "no-retrieval-date")).NpaState);
        Assert.Equal(AuthoritySnapshotState.SnapshotBuildFailureMissingMetadata, UsAuthoritySet.LoadProduction(Damaged("us.npa", "no-url")).NpaState);
        Assert.Equal(AuthoritySnapshotState.SnapshotBuildFailureUnsupportedVersion, UsAuthoritySet.LoadProduction(Damaged("us.npa", "version")).NpaState);
        Assert.Equal(AuthoritySnapshotState.SnapshotBuildFailureCountMismatch, UsAuthoritySet.LoadProduction(Damaged("us.npa", "count")).NpaState);
        Assert.Equal(AuthoritySnapshotState.SnapshotBuildFailureDuplicateOrInvalidNormalizedRecord, UsAuthoritySet.LoadProduction(Damaged("us.npa", "duplicate")).NpaState);
        Assert.Equal(AuthoritySnapshotState.SnapshotBuildFailureDuplicateOrInvalidNormalizedRecord, UsAuthoritySet.LoadProduction(Damaged("us.npa", "invalid-record")).NpaState);
        Assert.Equal(AuthoritySnapshotState.SnapshotBuildFailureDuplicateOrInvalidNormalizedRecord, UsAuthoritySet.LoadProduction(Damaged("us.npa", "contradictory-jurisdiction")).NpaState);
    }

    // ===== 6. a valid, self-consistent snapshot of insufficient scope or wrong ownership is not ready ========================

    [Fact]
    public void ANpaSnapshotMissingATerritoryOrAState_IsValidButNotSufficient_AndIsNotUsedAtAll()
    {
        foreach (string jurisdiction in new[] { "PR", "VI", "GU", "AS", "CNMI", "DC", "AK", "WY" })
        {
            var d = new Derived();
            d.Replace(UsAuthoritySet.NpaId, WithoutLines(d.Text(UsAuthoritySet.NpaId), l => l.EndsWith(" " + jurisdiction, StringComparison.Ordinal)));

            var set = UsAuthoritySet.LoadProduction(d);

            Assert.Equal(AuthoritySnapshotState.SnapshotValid, set.NpaState);        // the data itself is intact ...
            Assert.False(set.PhoneReady);                                             // ... but it does not cover the United States
            Assert.Contains($"us.npa jurisdiction: {jurisdiction}", set.ScopeIssues);
            Assert.Empty(Run(new UsPhoneCandidateDetector(set), "Phone: 202-555-0147 (415) 555-0100"));  // no half-used list
        }
    }

    [Fact]
    public void ANpaSnapshotWithAForeignCode_IsRejected_NotQuietlyBroadened()
    {
        var d = new Derived();
        d.Replace(UsAuthoritySet.NpaId, d.Text(UsAuthoritySet.NpaId) + "416 ON\n");

        var set = UsAuthoritySet.LoadProduction(d);

        Assert.False(set.PhoneReady);
        Assert.Contains("us.npa jurisdiction not allowed: ON", set.ScopeIssues);
        Assert.Empty(Run(new UsPhoneCandidateDetector(set), "416-555-0123 202-555-0147"));
    }

    [Fact]
    public void APlainShapeNpaSnapshot_HasNoJurisdictionToCheck_SoItIsNotProductionData()
    {
        var d = new Derived();
        d.Replace(UsAuthoritySet.NpaId, string.Concat(d.Text(UsAuthoritySet.NpaId).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l[..3] + "\n")));

        Assert.False(UsAuthoritySet.LoadProduction(d).PhoneReady);
        Assert.True(UsAuthoritySet.Load(d).PhoneReady);   // shape-only accepts it, labelled Provided -- never ProductionScope
        Assert.Equal(UsAuthorityOrigin.Provided, UsAuthoritySet.Load(d).Origin);
    }

    [Fact]
    public void AStateSnapshotMissingAPossessionOrWithAForeignCode_IsNotUsed()
    {
        foreach (string code in new[] { "PR", "VI", "GU", "AS", "MP", "DC", "CA" })
        {
            var d = new Derived();
            d.Replace(UsAuthoritySet.StateId, WithoutLines(d.Text(UsAuthoritySet.StateId), l => l == code));

            var set = UsAuthoritySet.LoadProduction(d);

            Assert.False(set.AddressReady);
            Assert.Contains($"usps.state code: {code}", set.ScopeIssues);
        }

        var foreign = new Derived();
        foreign.Replace(UsAuthoritySet.StateId, foreign.Text(UsAuthoritySet.StateId) + "ON\n");
        var foreignSet = UsAuthoritySet.LoadProduction(foreign);
        Assert.False(foreignSet.AddressReady);
        Assert.Contains("usps.state code not allowed: ON", foreignSet.ScopeIssues);
    }

    [Fact]
    public void ASuffixOrStateSnapshotWithARecordOfTheWrongShape_IsInvalid()
    {
        // a one-letter "suffix" or a three-letter "state" is not something the USPS tables contain
        foreach (string bad in new[] { "S", "ST1", "st" })
        {
            var suffix = new Derived();
            suffix.Replace(UsAuthoritySet.SuffixId, suffix.Text(UsAuthoritySet.SuffixId) + bad + "\n");
            Assert.Equal(AuthoritySnapshotState.SnapshotBuildFailureDuplicateOrInvalidNormalizedRecord, UsAuthoritySet.LoadProduction(suffix).SuffixState);
            Assert.False(UsAuthoritySet.LoadProduction(suffix).AddressReady);
        }

        var state = new Derived();
        state.Replace(UsAuthoritySet.StateId, state.Text(UsAuthoritySet.StateId) + "TXX\n");
        Assert.Equal(AuthoritySnapshotState.SnapshotBuildFailureDuplicateOrInvalidNormalizedRecord, UsAuthoritySet.LoadProduction(state).StateState);
    }

    [Fact]
    public void ASuffixSnapshotWithoutTheCoreSuffixes_IsNotUsed()
    {
        var d = new Derived();
        d.Replace(UsAuthoritySet.SuffixId, WithoutLines(d.Text(UsAuthoritySet.SuffixId), l => l is "ST" or "AVE"));

        var set = UsAuthoritySet.LoadProduction(d);

        Assert.False(set.AddressReady);
        Assert.Contains("usps.suffix anchor: ST", set.ScopeIssues);
        Assert.Contains("usps.suffix anchor: AVE", set.ScopeIssues);
    }

    // ===== 7. the rest of the product is unchanged ===========================================================================

    private static IDetector[] Legacy() =>
    [
        new PhoneDetector(), new EmailDetector(), new ResidentRegistrationNumberDetector(), new CardNumberDetector(),
        new BankAccountNumberDetector(), new SecretDetector(), new IpAddressDetector(), new MacAddressDetector(),
        new GpsCoordinateDetector(),
    ];

    private static string Key(DetectionCandidate c) => $"{c.PiiType}|{c.Span}|{c.Canonical?.Value}|{c.RiskLevel}|{c.Confidence}";

    [Fact]
    public void KoreanAndGlobal_AreUnchanged_WithTheRealData_WhetherTheUsPackIsOnOffOrUncertain()
    {
        var shipped = DetectionPipeline.CreateDefault();
        const string text = "연락처 010-1234-5678 이메일 user@example.test 901231-1234567 카드 4111 1111 1111 1111 서버 192.168.10.20";
        var expected = shipped.Detect(text).Candidates.OrderBy(c => c.Span.Start).Select(Key).ToList();
        Assert.True(expected.Count >= 4);

        foreach (var setting in new[]
                 { CountrySettingObservation.AuthenticatedOn, CountrySettingObservation.AuthenticatedOff, CountrySettingObservation.Missing,
                   CountrySettingObservation.Corrupt, CountrySettingObservation.Unauthenticated, CountrySettingObservation.UnknownVersion })
        {
            var pipeline = UsOptInPipeline.Create(Legacy(), unitedStatesSetting: setting, authority: Production);

            Assert.Equal(expected, pipeline.Detect(text).Candidates.OrderBy(c => c.Span.Start).Select(Key).ToList());
        }
    }

    [Fact]
    public void WithTheRealData_UnitedStatesOff_StopsAllThirteen_AndTheOtherSettingsKeepThemAll()
    {
        const string text =
            "Passport Number: P12345678 State: NY\nA-Number: A1234567\nI-94 Number: 123456789AB\nATIN: 912-93-4567\nITIN: 912-70-1234\n" +
            "SSN: 321-54-9876\nMBI: 1A23C45DE67\nSEVIS: N1234567890\nDriver License Number: D123-4567 State: CA\n" +
            "Routing Number: 120000003 | Account Number: AB12CD34\nPhone: 202-555-0147\nAddress: 123 Main Street\nFull Name: Alice O'Neil";

        Assert.Empty(UsOptInPipeline.Create(unitedStatesSetting: CountrySettingObservation.AuthenticatedOff, authority: Production).Detect(text).Candidates);
        foreach (var setting in new[]
                 { CountrySettingObservation.AuthenticatedOn, CountrySettingObservation.Missing, CountrySettingObservation.Corrupt,
                   CountrySettingObservation.Unauthenticated, CountrySettingObservation.UnknownVersion })
        {
            Assert.Equal(13, UsOptInPipeline.Create(unitedStatesSetting: setting, authority: Production).Detect(text).Candidates.Count);
        }
    }

    [Fact]
    public void WithTheRealData_TheEarlierElevenAreUnchanged_AndTheD3FixtureResultsAreAReducedViewOfTheRealOnes()
    {
        var real = UsOptInPipeline.Create(authority: Production);
        var fixture = UsOptInPipeline.Create(authority: UsTestAuthorityProvider.ValidSet());
        var eleven = UsOptInPipeline.Create();
        string[] texts =
        [
            "Phone: 212-555-1212; Address: 123 Main ST; Full Name: Alice Doe; SSN: 321-54-9876",
            "123 Main ST, New York, NY 10001", "(787) 555-1212", "ATIN: 912-88-4321 Name: Alice Doe Phone: 340.555.1212",
        ];

        foreach (string raw in texts)
        {
            var realFound = real.Detect(raw).Candidates.OrderBy(c => c.Span.Start).Select(Key).ToList();
            var fixtureFound = fixture.Detect(raw).Candidates.OrderBy(c => c.Span.Start).Select(Key).ToList();
            var elevenFound = eleven.Detect(raw).Candidates.OrderBy(c => c.Span.Start).Select(Key).ToList();

            Assert.Equal(fixtureFound, realFound);                                           // these inputs only use codes / states the fixture lists
            Assert.All(elevenFound, k => Assert.Contains(k, realFound));                      // nothing the eleven found is lost
        }
    }

    [Fact]
    public void LevelOnePolicyAndAliases_AreUnchanged_WithTheRealData()
    {
        string Rewrite(string raw)
        {
            var found = UsOptInPipeline.Create(authority: Production).Detect(raw);
            var decisions = CandidatePolicyEvaluator.Evaluate(ExceptionTrustedEvaluator.Evaluate(found, [], []));
            return AliasReplacer.Apply(raw, AliasAssigner.Assign(decisions, new AliasMap()));
        }

        Assert.Equal("Phone: [전화번호1]", Rewrite("Phone: 202-555-0147"));
        Assert.Equal("Address: [주소1]", Rewrite("Address: 123 Main Street"));
        Assert.Equal("[주소1]", Rewrite("123 Main Street, Austin, TX 78701"));
        Assert.Equal("Full Name: [이름1]", Rewrite("Full Name: Alice O'Neil"));
        Assert.Equal(CandidateDisposition.NeedsDecision, Assert.Single(CandidatePolicyEvaluator.Evaluate(ExceptionTrustedEvaluator.Evaluate(
            UsOptInPipeline.Create(authority: Production).Detect("Name: Alice O'Neil"), [], []))).Disposition);
        Assert.Equal(CandidateDisposition.NeedsDecision, Assert.Single(CandidatePolicyEvaluator.Evaluate(ExceptionTrustedEvaluator.Evaluate(
            UsOptInPipeline.Create(authority: Production).Detect("SSN: 321-54-9876"), [], []))).Disposition);
    }

    [Fact]
    public void ABoundedScan_WithTheRealData()
    {
        string line = "Phone: 202-555-0147; 123 Main Street, Austin, TX 78701; Address: 5 Pine Ct";
        string many = string.Join("\n", Enumerable.Repeat(line, 2_000));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var found = UsOptInPipeline.Create(authority: Production).Detect(many).Candidates;

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60));
        Assert.True(found.Count >= 2_000 * 3);
    }
}
