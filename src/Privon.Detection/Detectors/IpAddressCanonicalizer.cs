using System.Net;

namespace Privon.Detection.Detectors;

/// <summary>
/// Canonicalizes a parsed IP address via .NET's own IPAddress.ToString() rather than any
/// hand-rolled formatting -- this delegates RFC 5952 canonical IPv6 text representation
/// (lowercase, correct "::" compression, no leading zeros per group) and standard IPv4
/// dotted-decimal formatting to a battle-tested implementation instead of reimplementing it.
/// Different textual representations of the same address (e.g. "2001:0DB8::0001" and
/// "2001:db8::1") converge on the same canonical value; the raw matched text is never used
/// directly as the canonical value.
/// </summary>
internal static class IpAddressCanonicalizer
{
    public static CanonicalValue Canonicalize(IPAddress address) =>
        new(PiiType.IpAddress, address.ToString());
}
