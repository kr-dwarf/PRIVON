using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate E5G.P3 -- the single shared, pure calculation authority for the two
/// security-relevant values <see cref="NativeMessagingHostRegistrar"/> already froze (Gate E5D.B/C)
/// and <see cref="NativeMessagingHostRegistrationCoordinator"/> now also needs read-only: the exact
/// expected manifest path per browser, and the exact manifest JSON payload for a given spec.
///
/// MECHANICAL EXTRACTION ONLY (Gate E5G.P3): this type's two members are moved verbatim, byte-for-
/// byte, from the registrar's own former private methods of the same name/body -- neither the
/// Chrome/Edge path formula, the manifest field set, nor the JSON shape changed in any way.
/// <see cref="NativeMessagingHostRegistrar"/> now calls these same members instead of owning a second
/// copy, so the two can never independently drift; it delegates and adds nothing else. No
/// ownership/OWNED/STALE/FOREIGN/orphan judgment lives here -- that remains exclusively
/// <see cref="NativeMessagingHostRegistrar"/>'s (E5D, frozen) and, read-only, the coordinator's own
/// witness-only re-derivation of that same judgment.
/// </summary>
internal static class NativeMessagingHostRegistrationLayout
{
    /// <summary>The exact manifest path this registration contract expects to own for
    /// <paramref name="browser"/> (Gate E5D.B commander-frozen formula, unchanged) -- deterministic;
    /// no caller ever supplies a manifest path.</summary>
    internal static string ExpectedManifestPath(NativeMessagingBrowser browser)
    {
        string fileName = browser switch
        {
            NativeMessagingBrowser.Chrome => "chrome-host.json",
            NativeMessagingBrowser.Edge => "edge-host.json",
            _ => throw new ArgumentOutOfRangeException(nameof(browser), browser, "Unrecognized NativeMessagingBrowser value."),
        };

        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "PRIVON", "NativeMessaging", fileName);
    }

    /// <summary>Builds the complete, structurally valid Native Messaging manifest JSON for
    /// <paramref name="spec"/> via System.Text.Json (unchanged shape/fields, never manual string
    /// concatenation). Exactly one allowed_origins entry -- <paramref name="spec"/>'s own
    /// already-verified origin, never a production allowlist or any invented identity.</summary>
    internal static string BuildManifestJson(NativeMessagingHostRegistrationSpec spec) =>
        JsonSerializer.Serialize(new ManifestPayload(
            Name: "com.privon.host",
            Description: "PRIVON Native Messaging Host",
            Path: spec.HostExecutablePath,
            Type: "stdio",
            AllowedOrigins: [spec.ExtensionOrigin]));

    private sealed record ManifestPayload(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("allowed_origins")] string[] AllowedOrigins);
}
