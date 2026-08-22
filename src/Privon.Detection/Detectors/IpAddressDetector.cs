using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Privon.Core;

namespace Privon.Detection.Detectors;

/// <summary>
/// IPv4/IPv6 literal detector. A single PiiType (<see cref="PiiType.IpAddress"/>) covers both
/// families -- IPv4 and IPv6 are the same kind of PII, not two candidate types.
///
/// RiskLevel is fixed at Level2, not Level3: design doc decision 163 ("IP 주소: IPv4/IPv6가
/// 명확하면 온라인 식별정보로 보호한다") describes the same "structurally certain, auto-protect,
/// context-based exception possible" shape as the audit contract's Level 2 definition
/// ("확실한 개인정보는 기본 자동 보호"), and IP addresses are not a Level3 "고위험 비밀·금융·공식
/// 식별정보". Neither source document states an explicit "Level N" number for IP specifically
/// (unlike e.g. decision 161's explicit "Level 2" for 동·호수) -- this mapping is the closest
/// direct match to already-defined Level semantics, not a new policy invented here.
///
/// Deliberately does NOT hand-roll a full IPv6 grammar regex. Each pass only uses a regex to
/// find a *candidate zone* (a boundary-safe span of plausible characters); actual grammar
/// validation and canonicalization are delegated entirely to <see cref="IPAddress.TryParse"/>
/// -- .NET's own RFC 4291/5952-compliant parser -- never network/DNS APIs.
///
/// Private-use, loopback, link-local, and RFC 5737/3849 documentation-range addresses are all
/// still reported as candidates here. Detection's job is "this is an IP"; whether a given
/// context should exempt it (e.g. 127.0.0.1 in a local dev log) is explicitly a future
/// Policy/Context-layer decision, not this detector's. Similarly, a structurally valid IPv4
/// literal that could also be read as a version string (e.g. "1.2.3.4") is still reported --
/// this detector never guesses semantic intent; see the Phase 2K report's OPEN_QUESTION.
/// </summary>
public sealed class IpAddressDetector : IDetector
{
    public string Name => "IpAddressDetector";
    public PiiType PiiType => global::Privon.Detection.PiiType.IpAddress;

    // Candidate shape only (not a full 0-255 range check): 1-3 digits, no ambiguous leading
    // zero (rejects octal-looking forms like "010"). Per design preference, range validation
    // (0-255) happens as an explicit post-match parse step, not as regex range-alternation.
    private const string OctetShape = @"(?:0|[1-9]\d{0,2})";

    private static readonly Regex Ipv4CandidatePattern = new(
        OctetShape + @"\." + OctetShape + @"\." + OctetShape + @"\." + OctetShape,
        RegexOptions.Compiled);

    // Loose candidate-zone extraction only: any run of hex digits, ':', and '.'. Grammar
    // correctness (group count, "::" placement, IPv4-mapped tail) is entirely IPAddress's job.
    private static readonly Regex Ipv6RunPattern = new(@"[0-9A-Fa-f:.]+", RegexOptions.Compiled);

    public IReadOnlyList<DetectionCandidate> Detect(DetectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var results = new List<DetectionCandidate>();
        var ipv6Spans = new List<RawSpan>();

        // IPv6 first: an IPv4-mapped IPv6 literal (e.g. "::ffff:203.0.113.42") contains a
        // structurally valid IPv4 address as its tail, so IPv4 candidates that fall inside an
        // already-found IPv6 span are skipped below rather than double-reported.
        AddIpv6Candidates(context, results, ipv6Spans);
        AddIpv4Candidates(context, results, ipv6Spans);

        return results;
    }

    private void AddIpv4Candidates(DetectionContext context, List<DetectionCandidate> results, List<RawSpan> ipv6Spans)
    {
        foreach (Match m in Ipv4CandidatePattern.Matches(context.View.Text))
        {
            if (!HasCleanBoundary(context.View.Text, m.Index, m.Length, isIpv6: false)) continue;
            if (!TryParseIpv4(m.Value, out var address)) continue;

            var rawSpan = context.View.IndexMap.ToRawSpan(m.Index, m.Length);
            if (OverlapsAny(rawSpan, ipv6Spans)) continue;

            AddCandidate(rawSpan, address!, results);
        }
    }

    private void AddIpv6Candidates(DetectionContext context, List<DetectionCandidate> results, List<RawSpan> ipv6Spans)
    {
        foreach (Match m in Ipv6RunPattern.Matches(context.View.Text))
        {
            // Only a trailing run of '.' (ordinary sentence punctuation) is ever trimmed --
            // never a trailing ':' or hex digit. Trimming those would mean silently accepting
            // a truncated fragment of a longer, differently-shaped token instead of rejecting
            // it outright, which is exactly the partial-substring leak boundary hardening
            // exists to prevent elsewhere in this codebase.
            int length = m.Length;
            while (length > 0 && m.Value[length - 1] == '.') length--;
            if (length == 0) continue;

            var candidateText = m.Value[..length];
            if (!IsValidIpv6(candidateText, out var address)) continue;
            if (!HasCleanBoundary(context.View.Text, m.Index, length, isIpv6: true)) continue;

            var rawSpan = context.View.IndexMap.ToRawSpan(m.Index, length);
            AddCandidate(rawSpan, address!, results);
            ipv6Spans.Add(rawSpan);
        }
    }

    private void AddCandidate(RawSpan rawSpan, IPAddress address, List<DetectionCandidate> results)
    {
        var canonical = IpAddressCanonicalizer.Canonicalize(address);
        results.Add(new DetectionCandidate(
            global::Privon.Detection.PiiType.IpAddress,
            rawSpan,
            RiskLevel.Level2,
            DetectionConfidence.High,
            canonical,
            Name));
    }

    private static bool TryParseIpv4(string text, out IPAddress? address)
    {
        address = null;
        var parts = text.Split('.');
        if (parts.Length != 4) return false;
        foreach (var part in parts)
        {
            if (!int.TryParse(part, out var value) || value < 0 || value > 255) return false;
        }
        return IPAddress.TryParse(text, out address);
    }

    private static bool IsValidIpv6(string text, out IPAddress? address)
    {
        address = null;
        int colonCount = 0;
        foreach (var c in text)
        {
            if (c == ':') colonCount++;
        }
        if (colonCount < 2) return false; // cheap pre-filter, e.g. rejects "HH:MM" clock times

        if (!IPAddress.TryParse(text, out var parsed)) return false;
        if (parsed.AddressFamily != AddressFamily.InterNetworkV6) return false;

        address = parsed;
        return true;
    }

    private static bool OverlapsAny(RawSpan span, List<RawSpan> others)
    {
        foreach (var other in others)
        {
            if (span.OverlapsWith(other)) return true;
        }
        return false;
    }

    // Boundary hardening, same principle as Email/RRN/CardNumber/BankAccountNumber: a match is
    // only accepted if the character immediately outside it could not itself have extended the
    // token. For IPv4, a digit/letter/dot right outside means this is a fragment of a longer
    // token (e.g. "abc192.0.2.1xyz", "999192.0.2.1", "1.2.3.4.5"). For IPv6, ':' additionally
    // counts, since ':' is part of the address grammar itself.
    //
    // Phase 2O.1 -- IPv4 trailing-dot refinement: a leading '.' still always rejects (a digit
    // sitting just before an already-4-octet match can only mean the match is a cut-off
    // fragment of a longer run -- there is no ambiguity to resolve there). A *trailing* '.' is
    // different: on its own it says nothing about whether the "." is starting a genuine 5th
    // octet or is just ordinary trailing punctuation (most commonly a filename extension, e.g.
    // "192.0.2.1.log" -- see the Phase 2O filename-context report). So for IPv4 only, a
    // trailing '.' is boundary-extending exactly when a digit immediately follows it (that
    // digit could begin another octet, e.g. "192.0.2.1.5", "1.2.3.4.5") -- when it's anything
    // else (a letter, end of text, other punctuation) the '.' cannot be starting a numeric
    // continuation and the candidate is accepted. IPv6 boundary behavior is unchanged.
    private static bool HasCleanBoundary(string normalizedText, int start, int length, bool isIpv6)
    {
        if (start > 0 && IsBoundaryExtending(normalizedText[start - 1], isIpv6)) return false;

        int end = start + length;
        if (end >= normalizedText.Length) return true;

        char next = normalizedText[end];
        if (!isIpv6 && next == '.')
        {
            return !(end + 1 < normalizedText.Length && char.IsAsciiDigit(normalizedText[end + 1]));
        }

        return !IsBoundaryExtending(next, isIpv6);
    }

    private static bool IsBoundaryExtending(char c, bool isIpv6) =>
        char.IsAsciiLetterOrDigit(c) || c == '.' || (isIpv6 && c == ':');
}
