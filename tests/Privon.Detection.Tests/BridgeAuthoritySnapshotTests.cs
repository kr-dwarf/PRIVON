using System.Security.Cryptography;
using System.Text;
using Privon.Detection.Authority;
using Xunit;

namespace Privon.Detection.Tests;

/// <summary>
/// D3B0-B §17 -- the GENERIC authority-snapshot infrastructure.
///
/// SNAPSHOT_SAME_VALIDATOR: every test below substitutes only the PROVIDER and calls the same
/// production <see cref="AuthoritySnapshotLoader"/>. There is no test-only loader, validator,
/// normalizer or hash check. All record content is SYNTHETIC and contains no personal data.
/// </summary>
public sealed class BridgeAuthoritySnapshotTests
{
    private sealed class FakeProvider(byte[]? bytes, AuthoritySnapshotMetadata? metadata) : IAuthoritySnapshotProvider
    {
        public bool TryGetBytes(AuthoritySnapshotId id, out ReadOnlyMemory<byte> value)
        {
            value = bytes ?? ReadOnlyMemory<byte>.Empty;
            return bytes is not null;
        }

        public bool TryGetMetadata(AuthoritySnapshotId id, out AuthoritySnapshotMetadata value)
        {
            value = metadata!;
            return metadata is not null;
        }
    }

    private static AuthoritySnapshotId Id()
    {
        Assert.True(AuthoritySnapshotId.TryCreate("test.snapshot", out var id));
        return id;
    }

    private static byte[] Body(params string[] records) =>
        Encoding.UTF8.GetBytes(string.Concat(records.Select(r => r + "\n")));

    private static AuthoritySnapshotMetadata Meta(
        byte[] bytes, int count, string version = "1", string? hashOverride = null) =>
        new(Id(), version, "https://example.invalid/source", null, new DateOnly(2026, 1, 1),
            count, hashOverride ?? Convert.ToHexString(SHA256.HashData(bytes)));

    [Fact]
    public void ValidSnapshot_Loads()
    {
        var bytes = Body("AAA", "BBB", "CCC", "DDD");
        var ok = AuthoritySnapshotLoader.TryLoad(
            new FakeProvider(bytes, Meta(bytes, 4)), Id(), out var snapshot, out var validation);

        Assert.True(ok);
        Assert.Equal(AuthoritySnapshotState.SnapshotValid, validation.State);
        Assert.Equal(4, snapshot.NormalizedRecords.Count);
    }

    [Fact]
    public void MissingBytes_IsSnapshotMissing_AndNeverBroadens()
    {
        var ok = AuthoritySnapshotLoader.TryLoad(
            new FakeProvider(null, null), Id(), out var snapshot, out var validation);

        Assert.False(ok);
        Assert.Equal(AuthoritySnapshotState.SnapshotMissing, validation.State);
        Assert.Null(snapshot);   // no partial record set, no built-in default list
    }

    [Fact]
    public void MissingMetadataField_ReportsTheExactFieldName()
    {
        var bytes = Body("AAA");
        var metadata = new AuthoritySnapshotMetadata(
            Id(), "1", "https://example.invalid/source", null,
            default, 1, Convert.ToHexString(SHA256.HashData(bytes)));   // retrievalDate missing

        AuthoritySnapshotLoader.TryLoad(new FakeProvider(bytes, metadata), Id(), out _, out var validation);

        Assert.Equal(AuthoritySnapshotState.SnapshotBuildFailureMissingMetadata, validation.State);
        Assert.Equal("retrievalDate", validation.MissingMetadataField);
    }

    [Fact]
    public void UnsupportedVersion_FailsBeforeHash()
    {
        var bytes = Body("AAA");
        AuthoritySnapshotLoader.TryLoad(
            new FakeProvider(bytes, Meta(bytes, 1, version: "99")), Id(), out _, out var validation);
        Assert.Equal(AuthoritySnapshotState.SnapshotBuildFailureUnsupportedVersion, validation.State);
    }

    [Fact]
    public void HashMismatch_FailsClosed()
    {
        var bytes = Body("AAA");
        AuthoritySnapshotLoader.TryLoad(
            new FakeProvider(bytes, Meta(bytes, 1, hashOverride: new string('0', 64))),
            Id(), out _, out var validation);
        Assert.Equal(AuthoritySnapshotState.SnapshotBuildFailureHashMismatch, validation.State);
    }

    [Fact]
    public void DuplicateRecord_FailsBEFORETheCountCheck()
    {
        // Order 5-before-6 is REQUIRED by SNAP-NPA-DUPINVALID. The declared count here is
        // deliberately wrong too, so a 6-before-5 implementation would report CountMismatch.
        var bytes = Body("AAA", "AAA", "BBB");
        AuthoritySnapshotLoader.TryLoad(
            new FakeProvider(bytes, Meta(bytes, 99)), Id(), out _, out var validation);

        Assert.Equal(
            AuthoritySnapshotState.SnapshotBuildFailureDuplicateOrInvalidNormalizedRecord,
            validation.State);
    }

    [Fact]
    public void MissingFinalLf_IsAnInvalidNormalizedRecordSet()
    {
        var bytes = Encoding.UTF8.GetBytes("AAA\nBBB");
        AuthoritySnapshotLoader.TryLoad(
            new FakeProvider(bytes, Meta(bytes, 2)), Id(), out _, out var validation);
        Assert.Equal(
            AuthoritySnapshotState.SnapshotBuildFailureDuplicateOrInvalidNormalizedRecord,
            validation.State);
    }

    [Fact]
    public void CountMismatch_ReportsDeclaredAndObserved()
    {
        var bytes = Body("AAA", "BBB");
        AuthoritySnapshotLoader.TryLoad(
            new FakeProvider(bytes, Meta(bytes, 5)), Id(), out _, out var validation);

        Assert.Equal(AuthoritySnapshotState.SnapshotBuildFailureCountMismatch, validation.State);
        Assert.Equal(5, validation.DeclaredCount);
        Assert.Equal(2, validation.ObservedNormalizedCount);
    }

    [Fact]
    public void CrlfIsNormalizedToLf()
    {
        var bytes = Encoding.UTF8.GetBytes("AAA\r\nBBB\r\n");
        var ok = AuthoritySnapshotLoader.TryLoad(
            new FakeProvider(bytes, Meta(bytes, 2)), Id(), out var snapshot, out var validation);

        Assert.True(ok);
        Assert.Equal(AuthoritySnapshotState.SnapshotValid, validation.State);
        Assert.Equal(["AAA", "BBB"], snapshot.NormalizedRecords);
    }

    [Fact]
    public void NoImplicitValidState()
    {
        // AuthoritySnapshotState has no default that means Valid-by-omission other than the
        // explicit member, and the loader never returns a snapshot on a failure path.
        var bytes = Body("AAA");
        AuthoritySnapshotLoader.TryLoad(
            new FakeProvider(bytes, Meta(bytes, 2)), Id(), out var snapshot, out _);
        Assert.Null(snapshot);
    }
}
