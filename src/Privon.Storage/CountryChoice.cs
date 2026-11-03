using System.Text;
using System.Text.Json;

namespace Privon.Storage;

// [PRIVON-AI-HANDOFF]
// ROLE: The persisted per-country ON/OFF choice (T-1 storage foundation): its states, its origin marker and the strict document format.
// TRUTH: A record that reads back as On/Off is INTACT (AES-GCM under this Windows user's master key, bound to its own purpose, known version,
//        valid schema, not older than one already seen) and CLAIMS to come from a Settings user action. That claim is made by the code that wrote
//        it; the store cannot see a human. So a readable Off is "a verified record of a choice", never proof that a person made it, and it gives
//        no protection against code that runs as the same Windows user.
// FROZEN: Anything that is not an intact, known-version record from a known origin reads as a NON-choice (Missing / Corrupt / Unauthenticated /
//         UnknownVersion), and every non-choice means "the country pack stays active". Only an intact Off may stop a pack.
// DO_NOT: Add a write path without a real user action behind it, give a state to a record that failed any check, overwrite a record of an unknown
//         (newer) version, or put this in settings.bin (that file and its legacy readers are left byte-for-byte alone).
// NAVIGATE: PrivonLocalStore.LoadCountryChoice / SaveCountryChoice; the tools-side StoreBackedCountrySettingSource maps the state to the
//           detection scope's observation.

/// <summary>Where a persisted choice says it came from. <see cref="Unknown"/> is the zero value and is never written.</summary>
public enum CountryChoiceOrigin
{
    Unknown = 0,

    /// <summary>The writing code states the user chose this in the Settings UI. The product has NO such UI event yet (see the T-1 report).</summary>
    SettingsUserAction = 1,
}

/// <summary>What the store can state about one country's persisted choice. Everything other than <see cref="On"/> / <see cref="Off"/> is a non-choice.</summary>
public enum CountryChoiceState
{
    /// <summary>No record for this country (or no file at all).</summary>
    Missing = 0,

    /// <summary>A record exists but is not valid (truncated, malformed, duplicate or invalid entries).</summary>
    Corrupt,

    /// <summary>It could not be authenticated: no usable key, a failed authentication tag (tampering, another key or purpose), an unreadable file,
    /// an origin the store does not know, or a record older than one already seen in this process (a rollback).</summary>
    Unauthenticated,

    /// <summary>A well-authenticated record of a format version this build does not know. It is never interpreted and never overwritten.</summary>
    UnknownVersion,

    On,
    Off,
}

public readonly record struct CountryChoiceRead(CountryChoiceState State, long Revision);

internal static class CountryChoiceDocument
{
    public const int CurrentFormatVersion = 1;
    public const int MaxChoices = 64;
    public const string UserActionOrigin = "SettingsUserAction";

    internal sealed record Choice(string Country, bool Enabled, string Origin);

    internal sealed record Doc(long Revision, IReadOnlyList<Choice> Choices);

    internal enum ParseResult
    {
        Ok,
        Corrupt,
        UnknownVersion,
    }

    public static bool IsValidCountryId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 32 || !char.IsAsciiLetter(id[0])) return false;
        foreach (char c in id)
        {
            if (!char.IsAsciiLetterOrDigit(c)) return false;
        }

        return true;
    }

    public static (ParseResult Result, Doc? Doc) TryParse(byte[] plaintext)
    {
        try
        {
            using var document = JsonDocument.Parse(plaintext);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (ParseResult.Corrupt, null);
            if (!root.TryGetProperty("FormatVersion", out var versionElement) || !versionElement.TryGetInt32(out int version))
                return (ParseResult.Corrupt, null);
            if (version > CurrentFormatVersion) return (ParseResult.UnknownVersion, null);   // never interpreted
            if (version < CurrentFormatVersion) return (ParseResult.Corrupt, null);

            if (!root.TryGetProperty("Revision", out var revisionElement) || !revisionElement.TryGetInt64(out long revision) || revision < 1)
                return (ParseResult.Corrupt, null);
            if (!root.TryGetProperty("Choices", out var choicesElement) || choicesElement.ValueKind != JsonValueKind.Array
                || choicesElement.GetArrayLength() > MaxChoices)
                return (ParseResult.Corrupt, null);

            var choices = new List<Choice>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in choicesElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("Country", out var countryElement) || countryElement.ValueKind != JsonValueKind.String
                    || !item.TryGetProperty("Enabled", out var enabledElement) || enabledElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                    || !item.TryGetProperty("Origin", out var originElement) || originElement.ValueKind != JsonValueKind.String)
                    return (ParseResult.Corrupt, null);

                string? country = countryElement.GetString();
                if (!IsValidCountryId(country) || !seen.Add(country!)) return (ParseResult.Corrupt, null);
                choices.Add(new Choice(country!, enabledElement.GetBoolean(), originElement.GetString() ?? string.Empty));
            }

            return (ParseResult.Ok, new Doc(revision, choices));
        }
        catch (JsonException)
        {
            return (ParseResult.Corrupt, null);
        }
    }

    public static byte[] Serialize(Doc doc)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("FormatVersion", CurrentFormatVersion);
            writer.WriteNumber("Revision", doc.Revision);
            writer.WriteStartArray("Choices");
            foreach (var choice in doc.Choices)
            {
                writer.WriteStartObject();
                writer.WriteString("Country", choice.Country);
                writer.WriteBoolean("Enabled", choice.Enabled);
                writer.WriteString("Origin", choice.Origin);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }
}
