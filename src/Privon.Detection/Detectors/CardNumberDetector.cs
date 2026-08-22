using System.Linq;
using System.Text.RegularExpressions;
using Privon.Core;

namespace Privon.Detection.Detectors;

/// <summary>
/// Payment card number detector. Always Level3 -- RiskLevel and Confidence stay independent,
/// same contract as the other Level3 detectors.
///
/// Unlike ResidentRegistrationNumberDetector, a card-shaped run (four groups of four digits)
/// is much weaker structural evidence on its own -- order numbers, tracking numbers, and
/// bank-account-like strings are commonly grouped the same way. So here Luhn/context are
/// sometimes a hard *acceptance* gate, not just a confidence modifier: see AddFullCandidate
/// and AddMaskedCandidate for exactly when a candidate is discarded outright rather than kept
/// at a lower confidence.
///
/// Scope: 16 digits grouped 4-4-4-4 only (the shape this phase asks for). Other lengths
/// (Amex 15, Diners 14) are out of scope.
///
/// Two match shapes:
///   - Full: all 16 digits present -- Luhn-verifiable.
///   - Masked: at least one group is real digits and at least one group is "****" (e.g.
///     "1234-****-****-5678") -- Luhn cannot be computed, so an explicit card-context keyword
///     is required to accept the candidate at all.
/// </summary>
public sealed class CardNumberDetector : IDetector
{
    public string Name => "CardNumberDetector";
    public PiiType PiiType => global::Privon.Detection.PiiType.CardNumber;

    private const string GroupSep = @"[-\s]{0,3}";
    // Separator is required between groups for the masked shape -- an unformatted run mixing
    // digits and '*' with no separator at all is not a recognizable masked-card display.
    private const string GroupSepRequired = @"[-\s]{1,3}";

    private static readonly Regex FullPattern = new(
        @"(?<g1>\d{4})" + GroupSep + @"(?<g2>\d{4})" + GroupSep + @"(?<g3>\d{4})" + GroupSep + @"(?<g4>\d{4})",
        RegexOptions.Compiled);

    private static readonly Regex MaskedPattern = new(
        @"(?<g1>\d{4}|\*{4})" + GroupSepRequired + @"(?<g2>\d{4}|\*{4})" + GroupSepRequired
        + @"(?<g3>\d{4}|\*{4})" + GroupSepRequired + @"(?<g4>\d{4}|\*{4})",
        RegexOptions.Compiled);

    // Minimal, CardNumberDetector-local context signal -- same pattern and same "not a
    // general Context Engine" scope note as ResidentRegistrationNumberDetector's equivalent.
    private static readonly string[] ContextKeywords = ["카드번호", "카드 번호", "결제카드"];
    private const int ContextWindowChars = 15;

    public IReadOnlyList<DetectionCandidate> Detect(DetectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var results = new List<DetectionCandidate>();
        foreach (Match m in FullPattern.Matches(context.View.Text))
        {
            AddFullCandidate(context, m, results);
        }
        foreach (Match m in MaskedPattern.Matches(context.View.Text))
        {
            AddMaskedCandidate(context, m, results);
        }
        return results;
    }

    private void AddFullCandidate(DetectionContext context, Match m, List<DetectionCandidate> results)
    {
        if (!HasCleanBoundary(context.View.Text, m)) return;

        var digits = m.Groups["g1"].Value + m.Groups["g2"].Value + m.Groups["g3"].Value + m.Groups["g4"].Value;
        bool luhnValid = IsLuhnValid(digits);
        bool hasContext = HasExplicitCardContextKeyword(context.View.Text, m);

        // A plain 16-digit grouped number with no positive evidence either way (Luhn doesn't
        // match AND no explicit card context) is deliberately discarded, not just
        // confidence-capped -- the one case in this detector where a candidate is dropped
        // outright, to avoid over-detecting order/tracking/account-like numbers that happen
        // to be grouped in fours.
        if (!luhnValid && !hasContext) return;

        // Luhn actively failing on a fully-visible number is stronger negative evidence than
        // it merely being unavailable (the masked case) -- context alone keeps the candidate
        // from being dropped, but does not by itself override an active Luhn failure to reach
        // High. Only a Luhn match reaches High here.
        var confidence = luhnValid ? DetectionConfidence.High : DetectionConfidence.Medium;
        AddCandidate(context, m, confidence, results);
    }

    private void AddMaskedCandidate(DetectionContext context, Match m, List<DetectionCandidate> results)
    {
        var groups = new[] { m.Groups["g1"].Value, m.Groups["g2"].Value, m.Groups["g3"].Value, m.Groups["g4"].Value };
        bool allDigits = groups.All(g => g[0] != '*');
        bool allMasked = groups.All(g => g[0] == '*');
        // All-digit is FullPattern's job (avoids double-detecting the same span). All-masked
        // carries no digit evidence at all -- a generic "****-****-****-****" string must not
        // be treated as card evidence just because it happens to be grouped in fours.
        if (allDigits || allMasked) return;

        if (!HasCleanBoundary(context.View.Text, m)) return;

        // Luhn cannot be computed for a partially-masked number, so an explicit card-context
        // keyword is required to accept it at all.
        if (!HasExplicitCardContextKeyword(context.View.Text, m)) return;

        AddCandidate(context, m, DetectionConfidence.High, results);
    }

    private void AddCandidate(DetectionContext context, Match m, DetectionConfidence confidence, List<DetectionCandidate> results)
    {
        var rawSpan = context.View.IndexMap.ToRawSpan(m.Index, m.Length);
        var canonical = CardNumberCanonicalizer.Canonicalize(m);
        results.Add(new DetectionCandidate(
            global::Privon.Detection.PiiType.CardNumber,
            rawSpan,
            RiskLevel.Level3,
            confidence,
            canonical,
            Name));
    }

    private static bool IsLuhnValid(string digits)
    {
        int sum = 0;
        bool doubleDigit = false;
        for (int i = digits.Length - 1; i >= 0; i--)
        {
            int d = digits[i] - '0';
            if (doubleDigit)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
            doubleDigit = !doubleDigit;
        }
        return sum % 10 == 0;
    }

    private static bool HasExplicitCardContextKeyword(string normalizedText, Match m)
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

    // Boundary hardening, same principle as Email/RRN's HasCleanBoundary: a match is only
    // accepted if the character immediately outside it could not itself have extended the
    // digit/mask run -- otherwise a longer noise digit string would let the regex give up on
    // the leading/trailing extra digits and still report a clean-looking inner 16-digit
    // "candidate".
    private static bool HasCleanBoundary(string normalizedText, Match m)
    {
        if (m.Index > 0 && IsDigitOrMask(normalizedText[m.Index - 1])) return false;

        int endIndex = m.Index + m.Length;
        if (endIndex < normalizedText.Length && IsDigitOrMask(normalizedText[endIndex])) return false;

        return true;
    }

    private static bool IsDigitOrMask(char c) => char.IsAsciiDigit(c) || c == '*';
}
