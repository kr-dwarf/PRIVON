namespace Privon.Detection;

/// <summary>
/// Phase 2R.3 -- explicit, hand-maintained mapping between <see cref="PiiType"/> and a stable
/// persisted-identifier string. This is the ONLY place that translates a PiiType to/from a
/// string meant to be written to durable storage -- deliberately never
/// <c>enum.ToString()</c>/<c>Enum.Parse</c>/<c>Enum.TryParse</c>/reflection/ordinal, any of
/// which would silently corrupt persisted data the moment someone renames or reorders a
/// PiiType enum member. Every mapping entry below is written out by hand, once per direction,
/// specifically so a completeness/roundtrip regression test can catch a future PiiType value
/// added here without an accompanying stable-ID entry.
///
/// <see cref="Privon.Storage"/>'s <c>ExceptionEntry</c>/<c>TrustedPublicInfoEntry</c> (Phase
/// 2R.2) persist this identifier purely as an opaque string and never reference this codec or
/// PiiType at all -- Storage and Detection remain sibling projects (both depend only on
/// Privon.Core), and this codec does not create a new cross-project dependency in either
/// direction. The upper integration layer that eventually composes Storage + Detection is
/// responsible for calling into this codec to bridge a persisted <c>PiiTypeId</c> string to a
/// real <see cref="PiiType"/> (see the Phase 2R.3 report's ownership decision).
///
/// Matching is always exact, ordinal, case-sensitive, and un-trimmed -- "phone", "PHONE",
/// " Phone ", or any unrecognized string is never guessed into a known PiiType. An unknown
/// persisted identifier must never be silently treated as if it matched a different PII type;
/// see <see cref="TryFromStableId"/>.
/// </summary>
public static class PiiTypeIdCodec
{
    /// <summary>
    /// Converts a PiiType to its stable persisted identifier. Throws for any value not
    /// explicitly listed here (including an undefined/out-of-range enum value) -- a persisted
    /// identifier is never invented on the fly for an unmapped type.
    /// </summary>
    public static string ToStableId(PiiType type) => type switch
    {
        PiiType.Phone => "Phone",
        PiiType.Email => "Email",
        PiiType.ResidentRegistrationNumber => "ResidentRegistrationNumber",
        PiiType.CardNumber => "CardNumber",
        PiiType.BankAccountNumber => "BankAccountNumber",
        PiiType.Secret => "Secret",
        PiiType.IpAddress => "IpAddress",
        PiiType.MacAddress => "MacAddress",
        PiiType.GpsCoordinate => "GpsCoordinate",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "No stable persisted ID is defined for this PiiType."),
    };

    /// <summary>
    /// Attempts to resolve a persisted identifier back to a PiiType. Returns false -- never
    /// throws, never guesses -- for null, empty, whitespace-only, wrong-case, padded, or any
    /// other string that isn't an exact match for one of the identifiers this codec defines.
    /// </summary>
    public static bool TryFromStableId(string? id, out PiiType type)
    {
        switch (id)
        {
            case "Phone": type = PiiType.Phone; return true;
            case "Email": type = PiiType.Email; return true;
            case "ResidentRegistrationNumber": type = PiiType.ResidentRegistrationNumber; return true;
            case "CardNumber": type = PiiType.CardNumber; return true;
            case "BankAccountNumber": type = PiiType.BankAccountNumber; return true;
            case "Secret": type = PiiType.Secret; return true;
            case "IpAddress": type = PiiType.IpAddress; return true;
            case "MacAddress": type = PiiType.MacAddress; return true;
            case "GpsCoordinate": type = PiiType.GpsCoordinate; return true;
            default: type = default; return false;
        }
    }
}
