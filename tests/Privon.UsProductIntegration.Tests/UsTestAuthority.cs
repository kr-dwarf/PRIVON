using System.Text;
using Privon.Detection.Authority;
using Privon.UsProductIntegration;

namespace Privon.UsProductIntegration.Tests;

// SYNTHETIC TEST AUTHORITY. The four-entry subsets the frozen D3A page defines ("deterministic D3 authority-provider fixtures, not a
// claim that the production database has four records"), served through the production IAuthoritySnapshotProvider seam so the
// SAME AuthoritySnapshotLoader validates them. The product itself ships none of this data.
internal sealed class UsTestAuthorityProvider : IAuthoritySnapshotProvider
{
    // Provenance exactly as frozen on the D3A page (source URL, catalog URL, retrieval/freeze date, record count, SHA-256).
    public const string NpaUrl = "https://reports.nanpa.com/public/npa_report.csv";
    public const string NpaCatalogUrl = "https://nanpa.com/reports/npa-reports";
    public const string NpaSha256 = "077a93ebafb1c8e60606093ec9e2af95e62ec09e0569919d5a00b0dd14a05cad";
    public const string SuffixUrl = "https://pe.usps.com/text/pub28/28apc_002.htm";
    public const string SuffixSha256 = "68d77c59224b242b83f9b399b1b9730cb76d1a0073409845908127c730a4f0fe";
    public const string StateUrl = "https://pe.usps.com/text/pub28/pub28apb.htm";
    public const string StateSha256 = "7b33f17e3e98540a8e3f815dd83c0fc5da8912889a00b4dc0457286469210636";
    public static readonly DateOnly FreezeDate = new(2026, 9, 15);

    public const string NpaText = "212\n340\n787\n939\n";
    public const string SuffixText = "AVE\nBLVD\nRD\nST\n";
    public const string StateText = "CA\nNY\nPR\nVI\n";

    private readonly Dictionary<string, byte[]> _bytes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AuthoritySnapshotMetadata> _metadata = new(StringComparer.Ordinal);

    private UsTestAuthorityProvider()
    {
    }

    public static UsTestAuthorityProvider Valid()
    {
        var provider = new UsTestAuthorityProvider();
        provider.Set(UsAuthoritySet.NpaId, NpaText, NpaUrl, NpaCatalogUrl, NpaSha256, 4);
        provider.Set(UsAuthoritySet.SuffixId, SuffixText, SuffixUrl, null, SuffixSha256, 4);
        provider.Set(UsAuthoritySet.StateId, StateText, StateUrl, null, StateSha256, 4);
        return provider;
    }

    public static UsAuthoritySet ValidSet() => UsAuthoritySet.Load(Valid());

    public UsTestAuthorityProvider Without(AuthoritySnapshotId id)
    {
        _bytes.Remove(id.Value);
        return this;
    }

    /// <summary>Different bytes under the metadata of the original: the SHA-256 no longer matches.</summary>
    public UsTestAuthorityProvider Tampered(AuthoritySnapshotId id)
    {
        _bytes[id.Value] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(_bytes[id.Value]).Replace("\n", "\n\n", StringComparison.Ordinal));
        return this;
    }

    public UsTestAuthorityProvider WithDeclaredCount(AuthoritySnapshotId id, int count)
    {
        _metadata[id.Value] = _metadata[id.Value] with { ExpectedRecordCount = count };
        return this;
    }

    public UsTestAuthorityProvider WithoutRetrievalDate(AuthoritySnapshotId id)
    {
        _metadata[id.Value] = _metadata[id.Value] with { RetrievalDate = default };
        return this;
    }

    /// <summary>Replaces the content AND the matching metadata (a self-consistent but different snapshot).</summary>
    public UsTestAuthorityProvider WithContent(AuthoritySnapshotId id, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        int count = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        _bytes[id.Value] = bytes;
        _metadata[id.Value] = _metadata[id.Value] with
        {
            ExpectedRecordCount = count,
            ExpectedSha256Hex = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),
        };
        return this;
    }

    public bool TryGetBytes(AuthoritySnapshotId id, out ReadOnlyMemory<byte> bytes)
    {
        if (_bytes.TryGetValue(id.Value, out var value))
        {
            bytes = value;
            return true;
        }

        bytes = default;
        return false;
    }

    public bool TryGetMetadata(AuthoritySnapshotId id, out AuthoritySnapshotMetadata metadata)
    {
        if (_metadata.TryGetValue(id.Value, out var value))
        {
            metadata = value;
            return true;
        }

        metadata = null!;
        return false;
    }

    private void Set(AuthoritySnapshotId id, string text, string url, string? catalogUrl, string sha256, int count)
    {
        _bytes[id.Value] = Encoding.UTF8.GetBytes(text);
        _metadata[id.Value] = new AuthoritySnapshotMetadata(id, "1", url, catalogUrl, FreezeDate, count, sha256);
    }
}
