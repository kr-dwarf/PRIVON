using System.Security.Cryptography;
using System.Text;

namespace Privon.Detection.Authority;

/// <summary>
/// D3B0-A A.9 §20 -- the ONE production loader/validator. Generic infrastructure only: it holds
/// no U.S. authority content and no detector semantics.
///
/// SNAPSHOT_NO_PERMISSIVE_FALLBACK (§20.2): any state other than SnapshotValid means the
/// depending detector's Readiness is exactly UnavailableAuthoritySnapshot, it emits ZERO
/// candidates, and it MUST NOT broaden to a permissive fallback, a cached snapshot, a built-in
/// default list, a partial record set, or an allow-everything mode.
/// </summary>
public static class AuthoritySnapshotLoader
{
    /// <summary>
    /// §20.1 FROZEN validation order -- FIRST FAILURE WINS:
    /// (1) bytes present? else SnapshotMissing
    /// (2) metadata complete? else SnapshotBuildFailureMissingMetadata WITH the exact field name
    /// (3) Version supported? else SnapshotBuildFailureUnsupportedVersion
    /// (4) SHA-256 matches? else SnapshotBuildFailureHashMismatch
    /// (5) normalize; any duplicate OR invalid normalized record ⇒
    ///     SnapshotBuildFailureDuplicateOrInvalidNormalizedRecord
    /// (6) normalized count equals ExpectedRecordCount? else SnapshotBuildFailureCountMismatch
    /// (7) otherwise SnapshotValid
    ///
    /// Order 5-BEFORE-6 is REQUIRED by SNAP-NPA-DUPINVALID and must not be swapped.
    /// </summary>
    public static bool TryLoad(
        IAuthoritySnapshotProvider provider,
        AuthoritySnapshotId id,
        out AuthoritySnapshot snapshot,
        out AuthoritySnapshotValidation validation)
    {
        ArgumentNullException.ThrowIfNull(provider);
        snapshot = null!;

        // (1) bytes present?
        if (!provider.TryGetBytes(id, out var bytes))
        {
            validation = Fail(id, AuthoritySnapshotState.SnapshotMissing);
            return false;
        }

        // (2) metadata complete? -- report the EXACT missing field name.
        if (!provider.TryGetMetadata(id, out var metadata) || metadata is null)
        {
            validation = new AuthoritySnapshotValidation(
                id, AuthoritySnapshotState.SnapshotBuildFailureMissingMetadata, "metadata", null, null);
            return false;
        }

        string? missing = FirstMissingMetadataField(metadata);
        if (missing is not null)
        {
            validation = new AuthoritySnapshotValidation(
                id, AuthoritySnapshotState.SnapshotBuildFailureMissingMetadata, missing, null, null);
            return false;
        }

        // (3) version supported?
        if (!IsSupportedVersion(metadata.Version))
        {
            validation = Fail(id, AuthoritySnapshotState.SnapshotBuildFailureUnsupportedVersion);
            return false;
        }

        // (4) SHA-256 over the EXACT supplied bytes.
        string actual = Convert.ToHexString(SHA256.HashData(bytes.Span));
        if (!actual.Equals(metadata.ExpectedSha256Hex, StringComparison.OrdinalIgnoreCase))
        {
            validation = Fail(id, AuthoritySnapshotState.SnapshotBuildFailureHashMismatch);
            return false;
        }

        // (5) normalize: UTF-8/ASCII, LF endings, FINAL LF REQUIRED. Any duplicate OR invalid
        //     normalized record fails -- BEFORE the count check.
        if (!TryNormalize(bytes.Span, out var records))
        {
            validation = Fail(id, AuthoritySnapshotState.SnapshotBuildFailureDuplicateOrInvalidNormalizedRecord);
            return false;
        }

        // (6) declared count
        if (records.Count != metadata.ExpectedRecordCount)
        {
            validation = new AuthoritySnapshotValidation(
                id, AuthoritySnapshotState.SnapshotBuildFailureCountMismatch, null,
                metadata.ExpectedRecordCount, records.Count);
            return false;
        }

        // (7) valid
        snapshot = new AuthoritySnapshot(metadata, records);
        validation = new AuthoritySnapshotValidation(
            id, AuthoritySnapshotState.SnapshotValid, null, metadata.ExpectedRecordCount, records.Count);
        return true;
    }

    private static AuthoritySnapshotValidation Fail(AuthoritySnapshotId id, AuthoritySnapshotState state) =>
        new(id, state, null, null, null);

    // The field names are the literals the D3 fixtures use (e.g. "retrievalDate").
    private static string? FirstMissingMetadataField(AuthoritySnapshotMetadata m)
    {
        if (!m.Id.HasValue) return "id";
        if (string.IsNullOrWhiteSpace(m.Version)) return "version";
        if (string.IsNullOrWhiteSpace(m.SourceUrl)) return "sourceUrl";
        if (m.RetrievalDate == default) return "retrievalDate";
        if (m.ExpectedRecordCount < 0) return "expectedRecordCount";
        if (string.IsNullOrWhiteSpace(m.ExpectedSha256Hex)) return "expectedSha256Hex";
        return null;   // CatalogUrl is optional by declaration
    }

    private const string SupportedVersion = "1";

    private static bool IsSupportedVersion(string version) =>
        string.Equals(version, SupportedVersion, StringComparison.Ordinal);

    /// <summary>
    /// UTF-8/ASCII decode with THROWING fallback (no replacement), CRLF/CR folded to LF, and a
    /// REQUIRED final LF. Returns false for any duplicate or invalid normalized record.
    /// </summary>
    private static bool TryNormalize(ReadOnlySpan<byte> bytes, out IReadOnlyList<string> records)
    {
        records = [];
        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        if (text.Length == 0) return false;
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (!text.EndsWith('\n')) return false;   // final LF required

        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0) continue;                 // the trailing empty after the final LF
            if (line.Trim().Length != line.Length) return false;   // invalid: padded record
            if (!seen.Add(line)) return false;                     // duplicate
            list.Add(line);
        }

        records = list;
        return true;
    }
}
