namespace Privon.Detection.Detectors;

/// <summary>
/// Canonicalizes a validated 6-group MAC address to a single deterministic textual form:
/// lowercase hex, colon-separated (e.g. "02:00:00:00:00:01"). IEEE's own EUI-48 guidance
/// permits either colon or hyphen as the group separator and does not mandate a single case
/// for display, so this is an explicit implementation choice (not a new security policy) --
/// colon+lowercase matches the form used by RFC 5342 examples and by the most common Unix
/// tooling (ip/ifconfig), giving one deterministic CanonicalValue regardless of which
/// separator or case the raw text used.
/// </summary>
internal static class MacAddressCanonicalizer
{
    public static CanonicalValue Canonicalize(string[] hexGroups)
    {
        var lowered = new string[hexGroups.Length];
        for (int i = 0; i < hexGroups.Length; i++)
        {
            lowered[i] = hexGroups[i].ToLowerInvariant();
        }
        return new CanonicalValue(PiiType.MacAddress, string.Join(':', lowered));
    }
}
