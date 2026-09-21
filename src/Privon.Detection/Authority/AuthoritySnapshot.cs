namespace Privon.Detection.Authority;

/// <summary>D3B0-A A.9 §20 -- an authority snapshot's stable identifier.</summary>
public readonly record struct AuthoritySnapshotId
{
    private readonly string? _value;
    private AuthoritySnapshotId(string value) => _value = value;

    public string Value => _value!;
    public bool HasValue => _value is not null;

    public static bool TryCreate(string? value, out AuthoritySnapshotId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        id = new AuthoritySnapshotId(value);
        return true;
    }

    public override string ToString() => _value ?? string.Empty;
}

/// <summary>D3B0-A A.9 §20 -- declared provenance for one snapshot. Contains no personal data.</summary>
public sealed record AuthoritySnapshotMetadata(
    AuthoritySnapshotId Id,
    string Version,
    string SourceUrl,
    string? CatalogUrl,
    DateOnly RetrievalDate,
    int ExpectedRecordCount,
    string ExpectedSha256Hex);

/// <summary>
/// D3B0-A A.9 §20 -- the production seam a test substitutes.
///
/// SNAPSHOT_SAME_VALIDATOR: a test supplies a different provider and calls the SAME production
/// <see cref="AuthoritySnapshotLoader"/>. A test-only loader, validator, normalizer or hash check
/// is a contract violation.
/// </summary>
public interface IAuthoritySnapshotProvider
{
    bool TryGetBytes(AuthoritySnapshotId id, out ReadOnlyMemory<byte> bytes);
    bool TryGetMetadata(AuthoritySnapshotId id, out AuthoritySnapshotMetadata metadata);
}

/// <summary>D3B0-A A.9 §20.1 -- closed validation-state domain. There is NO implicit Valid.</summary>
public enum AuthoritySnapshotState
{
    SnapshotValid = 0,
    SnapshotMissing = 1,
    SnapshotBuildFailureMissingMetadata = 2,
    SnapshotBuildFailureCountMismatch = 3,
    SnapshotBuildFailureDuplicateOrInvalidNormalizedRecord = 4,
    SnapshotBuildFailureHashMismatch = 5,
    SnapshotBuildFailureUnsupportedVersion = 6,
}

/// <summary>D3B0-A A.9 §20 -- the observable validation outcome, including WHY it failed.</summary>
public sealed record AuthoritySnapshotValidation(
    AuthoritySnapshotId Id,
    AuthoritySnapshotState State,
    string? MissingMetadataField,
    int? DeclaredCount,
    int? ObservedNormalizedCount);

/// <summary>D3B0-A A.9 §20 -- a validated snapshot. Only ever produced for SnapshotValid.</summary>
public sealed record AuthoritySnapshot(
    AuthoritySnapshotMetadata Metadata,
    IReadOnlyList<string> NormalizedRecords);
