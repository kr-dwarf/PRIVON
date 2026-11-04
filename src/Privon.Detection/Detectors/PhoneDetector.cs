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
    // ---- D3B0-A A.9 §5.2 frozen legacy identity/rank/evidence. Literals, never
    // ---- inferred from the class name, registration order, or list position
    // ---- (DETECTOR_IDENTITY_IS_EXPLICIT, §5.1). Detect(), the regex, confidence
    // ---- computation, the canonicalizer and span arithmetic are UNCHANGED.
    public DetectorId Id { get; } = DetectorId.Create("kr.phone");

    public int ResolutionRank => 300;

    // §16.2: this detector depends on no authority snapshot, so it is literally
    // Ready. Stated rather than defaulted precisely because a default of Ready
    // would be the fail-open direction (§6).
    public DetectorReadinessState Readiness => DetectorReadinessState.Ready;


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
                Name,
                // DETECTOR_MUST_STAMP_IDENTITY (§7.1): this detector passes its own DetectorId,
                // ResolutionRank and ResolutionEvidence EXPLICITLY. The parameter defaults exist
                // for harness-injected candidates only; a production detector left on defaults
                // is a contract violation.
                DetectorId: Id,
                Evidence: ResolutionEvidence.S,
                ResolutionRank: ResolutionRank));
        }
        return results;
    }
}
