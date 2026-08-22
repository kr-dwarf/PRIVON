using System.Text.RegularExpressions;
using Privon.Core;

namespace Privon.Detection.Detectors;

/// <summary>
/// MAC (EUI-48) address detector. Only the two textual forms with a direct IEEE/IETF primary
/// source are supported:
///   - colon-hexadecimal, e.g. "02:00:00:00:00:01" (IEEE 802 canonical form; also RFC 5342's
///     "MAC-48" textual representation)
///   - hyphen-hexadecimal, e.g. "02-00-00-00-00-01" (also an IEEE 802 canonical form; the
///     separator used by RFC 7042's documentation-range examples)
/// Both are six groups of exactly two hex digits, upper- or lower-case, group count fixed at
/// six (48 bits / 8 bits-per-octet). The "dotted" four-hex-digit-group form some vendor tools
/// display (e.g. Cisco IOS's "AAAA.BBBB.CCCC") has no IEEE/IETF primary-standard basis found
/// during Phase 2L research -- it is a vendor display convention, not a standardized textual
/// form -- so it is deliberately NOT supported here per the "공식 근거 없는 형식은 추가하지
/// 않는다" constraint.
///
/// EUI-64 is a distinct, separately-assigned 64-bit identifier (8 octets / 16 hex digits, not
/// 6/12) -- structurally a different shape from a MAC-48/EUI-48 address, so it is out of scope
/// for this detector by construction, not by special-casing. It is not added as a supported
/// form here; see the Phase 2L report's OPEN_QUESTION.
///
/// RiskLevel is fixed at Level2, not Level3: design doc decision 164 ("MAC 주소·기기 ID·광고
/// ID: 장치 식별자로 문맥이 명확하면 보호한다") has the same "structurally certain, auto-
/// protect" shape as decision 163 (IP address, already mapped to Level2 -- see
/// IpAddressDetector) and the audit contract's Level 2 definition ("확실한 개인정보는 기본
/// 자동 보호"). It is not one of the audit contract's enumerated Level 3 고위험 items
/// (주민등록번호/카드/계좌/비밀번호/API Key/Token/Secret). Neither source document states an
/// explicit "Level N" number for MAC specifically -- this mapping follows the same
/// already-established interpretation precedent as IP, not a new policy invented here.
///
/// Per decision 164 ("탐지기는 독립 모듈로 구현한다"), this detector does not call into any
/// other detector and is not called into by any other detector.
///
/// Locally-administered and multicast MAC addresses are still reported as ordinary
/// candidates -- this detector's job is "this is a MAC address structurally", never "should
/// this specific bit-pattern be protected in this context". That judgment belongs to a future
/// Policy/Context layer, matching the same Detection/Policy split already used for
/// private-range and loopback IP addresses in IpAddressDetector.
///
/// Deliberately does NOT hand-roll a single loose regex covering both separators and every
/// group-count edge case. Each pass matches only a candidate zone of the exact expected shape
/// (six 2-hex-digit groups joined by one fixed separator); boundary hardening then confirms
/// the match is not a fragment of a longer hex/separator run before it is accepted.
/// </summary>
public sealed class MacAddressDetector : IDetector
{
    public string Name => "MacAddressDetector";
    public PiiType PiiType => global::Privon.Detection.PiiType.MacAddress;

    private const string HexGroup = "[0-9A-Fa-f]{2}";

    private static readonly Regex ColonPattern = new(
        HexGroup + @"(?::" + HexGroup + @"){5}",
        RegexOptions.Compiled);

    private static readonly Regex HyphenPattern = new(
        HexGroup + @"(?:-" + HexGroup + @"){5}",
        RegexOptions.Compiled);

    public IReadOnlyList<DetectionCandidate> Detect(DetectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var results = new List<DetectionCandidate>();
        AddCandidates(context, ColonPattern, ':', results);
        AddCandidates(context, HyphenPattern, '-', results);
        return results;
    }

    private void AddCandidates(DetectionContext context, Regex pattern, char separator, List<DetectionCandidate> results)
    {
        var text = context.View.Text;
        foreach (Match m in pattern.Matches(text))
        {
            if (!HasCleanBoundary(text, m.Index, m.Length, separator)) continue;

            var rawSpan = context.View.IndexMap.ToRawSpan(m.Index, m.Length);
            var hexGroups = m.Value.Split(separator);
            var canonical = MacAddressCanonicalizer.Canonicalize(hexGroups);

            results.Add(new DetectionCandidate(
                global::Privon.Detection.PiiType.MacAddress,
                rawSpan,
                RiskLevel.Level2,
                DetectionConfidence.High,
                canonical,
                Name));
        }
    }

    // Boundary hardening, same principle as IpAddressDetector: a match is only accepted if the
    // character immediately outside it could not itself have extended the token -- a hex digit
    // right outside means this is a fragment of a longer hex run (e.g. "AA02:...:01BB" cutting
    // out the middle "02:...:01"), and the *same* separator character right outside means there
    // is a 7th group, i.e. too many groups (e.g. "AA:BB:CC:DD:EE:FF:11").
    private static bool HasCleanBoundary(string normalizedText, int start, int length, char separator)
    {
        if (start > 0 && IsBoundaryExtending(normalizedText[start - 1], separator)) return false;

        int end = start + length;
        if (end < normalizedText.Length && IsBoundaryExtending(normalizedText[end], separator)) return false;

        return true;
    }

    private static bool IsBoundaryExtending(char c, char separator) =>
        char.IsAsciiHexDigit(c) || c == separator;
}
