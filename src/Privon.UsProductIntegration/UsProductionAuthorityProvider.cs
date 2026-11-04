using System.Reflection;
using System.Text.Json;
using Privon.Detection.Authority;

namespace Privon.UsProductIntegration;

// ISOLATED PRODUCT-INTEGRATION CANDIDATE -- NOT REGISTERED, NOT ACTIVATED. See UsCandidateFactory.cs.
//
// Serves the authority artifact embedded in this assembly (AuthorityData/): three normalized snapshots built from PUBLIC NANPA and USPS
// files that were read once, read-only, and are kept verbatim next to them. The product's AuthoritySnapshotLoader is what verifies them
// (metadata, version, SHA-256, duplicates, count); this class only hands over bytes and metadata. It never reads a file path, never calls
// the network, and a missing / unreadable manifest or resource simply makes that snapshot "missing" -- it never throws and never invents data.
//
// The embedded manifest is the metadata source (URL, retrieval date, record count, SHA-256 of the normalized bytes, artifact version). The
// source file hashes in it are provenance for the verbatim sources; the test suite re-derives the normalized data from those sources.

/// <summary>Bytes + metadata of the embedded official-source authority snapshots, for <see cref="UsAuthoritySet.LoadProduction"/>.</summary>
public sealed class UsProductionAuthorityProvider : IAuthoritySnapshotProvider
{
    private const string Prefix = "Privon.UsProductIntegration.AuthorityData.";

    private readonly Dictionary<string, byte[]> _bytes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AuthoritySnapshotMetadata> _metadata = new(StringComparer.Ordinal);

    public UsProductionAuthorityProvider() : this(typeof(UsProductionAuthorityProvider).Assembly)
    {
    }

    internal UsProductionAuthorityProvider(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        try
        {
            using var manifestStream = assembly.GetManifestResourceStream(Prefix + "authority-manifest.json");
            if (manifestStream is null)
            {
                return;
            }

            using var document = JsonDocument.Parse(manifestStream);
            var root = document.RootElement;
            ArtifactVersion = root.TryGetProperty("artifactVersion", out var version) ? version.GetString() : null;

            foreach (var entry in root.GetProperty("snapshots").EnumerateArray())
            {
                string? id = entry.GetProperty("id").GetString();
                string? file = entry.GetProperty("normalizedFile").GetString();
                if (!AuthoritySnapshotId.TryCreate(id, out var snapshotId) || file is null)
                {
                    continue;
                }

                using var data = assembly.GetManifestResourceStream(Prefix + Path.GetFileName(file));
                if (data is null)
                {
                    continue;
                }

                using var buffer = new MemoryStream();
                data.CopyTo(buffer);
                _bytes[snapshotId.Value] = buffer.ToArray();

                DateOnly retrieved = DateOnly.TryParseExact(entry.GetProperty("retrievedOn").GetString(), "yyyy-MM-dd", out var date) ? date : default;
                _metadata[snapshotId.Value] = new AuthoritySnapshotMetadata(
                    snapshotId,
                    entry.GetProperty("version").GetString() ?? string.Empty,
                    entry.GetProperty("sourceUrl").GetString() ?? string.Empty,
                    entry.TryGetProperty("catalogUrl", out var catalog) ? catalog.GetString() : null,
                    retrieved,
                    entry.GetProperty("normalizedRecordCount").GetInt32(),
                    entry.GetProperty("normalizedSha256").GetString() ?? string.Empty);
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or IOException)
        {
            // An unreadable artifact provides nothing: every snapshot is then "missing" and the depending detectors are not ready.
            _bytes.Clear();
            _metadata.Clear();
            ArtifactVersion = null;
        }
    }

    /// <summary>The artifact version recorded in the manifest (null if the manifest could not be read).</summary>
    public string? ArtifactVersion { get; private set; }

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
}
