using System.Text.RegularExpressions;
using Privon.Core;

namespace Privon.Detection.Detectors;

/// <summary>
/// Korean resident registration number (주민등록번호) detector. Always Level3 -- RiskLevel and
/// Confidence are kept strictly independent, per the design contract: no evidentiary signal
/// below ever removes a candidate or downgrades its RiskLevel, since a typo or a modern-format
/// number is still a leak risk PRIVON must not silently let through.
///
/// Two match shapes, sharing the same date+gender prefix:
///   - Full: all 13 digits present.
///   - Masked: gender digit visible, the remaining 6 digits redacted with '*'/'x'/'X' (e.g.
///     "900101-1******").
/// Structural gates that ARE hard requirements: the first 6 digits must be a plausible
/// calendar date, and the digit right after the separator must be a plausible gender/century
/// code (1-8) -- these define what counts as "RRN-shaped" in the first place, so a plain
/// 13-digit number (order numbers, EAN/ISBN, timestamps) is rejected outright rather than
/// accepted at low confidence.
///
/// Phase 2C.1 -- Modern-Format Hardening: Korea's Oct-2020 RRN issuance change randomized the
/// back 6 digits (including the position that used to be a deterministic check digit) for
/// numbers issued or re-issued from that point on. <see cref="MatchesLegacyChecksumPattern"/>
/// is therefore named for exactly what it checks -- whether the number matches the pre-2020
/// weighted-sum pattern -- and is never described as "checksum valid" or "valid RRN", since a
/// mismatch says nothing about whether a modern-format number is real. It remains one
/// evidentiary signal among several (see AddCandidate), never a filter.
///
/// TODO(RRN_VS_ALIEN_REGISTRATION_CLASSIFICATION): the gender/century digit range accepted
/// here (1-8) protects the same 13-digit shape used by alien registration numbers (외국인등록번호),
/// which are explicitly out of scope for this phase. Protection is intentionally NOT narrowed
/// to avoid shrinking Level3 coverage; when a dedicated AlienRegistrationNumber detector is
/// built, detector responsibility should be split by the two IDs' official format rules
/// (see docs/decisions and the design doc's PII catalog) rather than by narrowing this one.
/// </summary>
public sealed class ResidentRegistrationNumberDetector : IDetector
{
    public string Name => "ResidentRegistrationNumberDetector";
    public PiiType PiiType => global::Privon.Detection.PiiType.ResidentRegistrationNumber;

    // Shared date+separator+gender prefix; the two patterns below only differ in what follows.
    // NOTE (TODO RRN_VS_ALIEN_REGISTRATION_CLASSIFICATION): [1-8] intentionally also matches
    // the gender/century digit shape used by alien registration numbers -- see class doc.
    private const string DatePrefix =
        @"(?<yy>\d{2})(?<mm>0[1-9]|1[0-2])(?<dd>0[1-9]|[12]\d|3[01])[-\s]{0,3}(?<gender>[1-8])";

    private static readonly Regex FullPattern = new(DatePrefix + @"(?<back>\d{6})", RegexOptions.Compiled);
    private static readonly Regex MaskedPattern = new(DatePrefix + @"(?<back>[*xX]{6})", RegexOptions.Compiled);

    private static readonly int[] LegacyChecksumWeights = [2, 3, 4, 5, 6, 7, 8, 9, 2, 3, 4, 5];
    private static readonly int[] DaysInMonth = [31, 29, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];

    // Minimal, RRN-detector-local context signal -- explicitly NOT a general cross-detector
    // Context Engine. TODO: once a shared Context Engine exists for Detection as a whole
    // (surrounding-keyword evidence is useful for other PII types too, not just RRN), replace
    // this local check with that shared mechanism instead of extending it here.
    private static readonly string[] ContextKeywords = ["주민등록번호", "주민번호"];
    private const int ContextWindowChars = 15;

    public IReadOnlyList<DetectionCandidate> Detect(DetectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var results = new List<DetectionCandidate>();
        foreach (Match m in FullPattern.Matches(context.View.Text))
        {
            AddCandidate(context, m, isMasked: false, results);
        }
        foreach (Match m in MaskedPattern.Matches(context.View.Text))
        {
            AddCandidate(context, m, isMasked: true, results);
        }
        return results;
    }

    private void AddCandidate(DetectionContext context, Match m, bool isMasked, List<DetectionCandidate> results)
    {
        if (!IsPlausibleDate(m.Groups["mm"].Value, m.Groups["dd"].Value)) return;
        if (!HasCleanBoundary(context.View.Text, m)) return;

        // Every structurally-gated candidate is at least Medium (never silently downgraded to
        // Low just for being masked or not matching the legacy pattern). Confidence is raised
        // to High by either of two independent signals -- a legacy-checksum-pattern match, or
        // an explicit "주민번호"/"주민등록번호" keyword nearby -- neither of which is a hard
        // requirement, and neither of which is used to drop a candidate when absent.
        var legacyPatternMatch = !isMasked && MatchesLegacyChecksumPattern(m);
        var hasExplicitContext = HasExplicitRrnContextKeyword(context.View.Text, m);

        var confidence = (legacyPatternMatch || hasExplicitContext)
            ? DetectionConfidence.High
            : DetectionConfidence.Medium;

        var rawSpan = context.View.IndexMap.ToRawSpan(m.Index, m.Length);
        var canonical = ResidentRegistrationNumberCanonicalizer.Canonicalize(m);
        results.Add(new DetectionCandidate(
            global::Privon.Detection.PiiType.ResidentRegistrationNumber,
            rawSpan,
            RiskLevel.Level3,
            confidence,
            canonical,
            Name));
    }

    private static bool IsPlausibleDate(string mm, string dd)
    {
        int month = (mm[0] - '0') * 10 + (mm[1] - '0');
        int day = (dd[0] - '0') * 10 + (dd[1] - '0');
        return day >= 1 && day <= DaysInMonth[month - 1];
    }

    /// <summary>
    /// Whether the 13 digits match the pre-Oct-2020 weighted-sum pattern (weights
    /// [2,3,4,5,6,7,8,9,2,3,4,5] over digits 1-12, mod 11, compared against digit 13).
    /// Deliberately NOT named "IsValid"/"ChecksumValid": since Oct 2020 Korea randomizes the
    /// back 6 digits for newly issued/reissued numbers, a mismatch here does not mean the
    /// number is fake, and a match does not confirm it is real -- this is one auxiliary
    /// confidence signal among several (see AddCandidate), never a correctness filter.
    /// </summary>
    private static bool MatchesLegacyChecksumPattern(Match m)
    {
        var combined = m.Groups["yy"].Value + m.Groups["mm"].Value + m.Groups["dd"].Value
            + m.Groups["gender"].Value + m.Groups["back"].Value;

        int sum = 0;
        for (int i = 0; i < LegacyChecksumWeights.Length; i++)
        {
            sum += (combined[i] - '0') * LegacyChecksumWeights[i];
        }
        int check = (11 - (sum % 11)) % 10;
        return check == combined[12] - '0';
    }

    private static bool HasExplicitRrnContextKeyword(string normalizedText, Match m)
    {
        int windowStart = Math.Max(0, m.Index - ContextWindowChars);
        int windowEnd = Math.Min(normalizedText.Length, m.Index + m.Length + ContextWindowChars);
        var window = normalizedText[windowStart..windowEnd];

        foreach (var keyword in ContextKeywords)
        {
            if (window.Contains(keyword, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    // Boundary hardening, same principle as EmailDetector's HasCleanBoundary: a match is only
    // accepted if the character immediately outside it could not itself have extended the
    // digit/mask run. Without this, a 19-digit noise string would let the regex give up on
    // the leading/trailing extra digits and still report a clean-looking inner 13-digit
    // "candidate" -- exactly the partial-substring leak this phase must prevent.
    private static bool HasCleanBoundary(string normalizedText, Match m)
    {
        if (m.Index > 0 && IsDigitOrMask(normalizedText[m.Index - 1])) return false;

        int endIndex = m.Index + m.Length;
        if (endIndex < normalizedText.Length && IsDigitOrMask(normalizedText[endIndex])) return false;

        return true;
    }

    private static bool IsDigitOrMask(char c) => char.IsAsciiDigit(c) || c is '*' or 'x' or 'X';
}
