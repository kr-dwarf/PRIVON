using System.Text.RegularExpressions;
using Privon.Core;

namespace Privon.Detection.Detectors;

/// <summary>
/// Korean phone numbers only (mobile 010, Seoul 02, area codes 031-064, VoIP 070, virtual
/// 050x), with or without the +82 country code. Deterministic detector -- Level 2, High
/// confidence for every structural match; there is no "weak" phone match in this design.
///
/// The prefix alternation is the primary false-positive guard: order numbers, document
/// numbers, dates, amounts, generic digit runs, IPs (dot-separated), and version numbers
/// (dot-separated) do not begin with a recognized Korean phone prefix, so they never match.
/// </summary>
public sealed class PhoneDetector : IDetector
{
    public string Name => "PhoneDetector";
    public PiiType PiiType => global::Privon.Detection.PiiType.Phone;

    // Area/mobile codes: 02 (Seoul); 031-033/041-044/051-055/061-064 (regional); 010
    // (mobile); 070 (VoIP); 050,0502-0509 (virtual numbers).
    private const string CodeAlternation = "2|3[1-3]|4[1-4]|5[1-5]|6[1-4]|10|70|50[2-9]?";

    private static readonly Regex Pattern = new(
        $@"(?<!\d)(?:(?<intl>\+82[-\s]?(?:{CodeAlternation}))|(?<dom>0(?:{CodeAlternation})))" +
        @"[-\s]{0,3}(?<mid>\d{3,4})[-\s]{0,3}(?<last>\d{4})(?!\d)",
        RegexOptions.Compiled);

    public IReadOnlyList<DetectionCandidate> Detect(DetectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var results = new List<DetectionCandidate>();
        foreach (Match m in Pattern.Matches(context.View.Text))
        {
            var rawSpan = context.View.IndexMap.ToRawSpan(m.Index, m.Length);
            var canonical = PhoneCanonicalizer.Canonicalize(m);
            results.Add(new DetectionCandidate(
                global::Privon.Detection.PiiType.Phone,
                rawSpan,
                RiskLevel.Level2,
                DetectionConfidence.High,
                canonical,
                Name));
        }
        return results;
    }
}
