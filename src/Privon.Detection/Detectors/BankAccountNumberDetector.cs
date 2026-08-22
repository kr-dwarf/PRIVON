using System.Linq;
using System.Text.RegularExpressions;
using Privon.Core;

namespace Privon.Detection.Detectors;

/// <summary>
/// Korean bank account number detector. Context-heavy by design: unlike Phone/RRN/CardNumber,
/// there is no universal cross-bank checksum or fixed digit-length rule to lean on (this
/// phase deliberately does not hardcode any bank-specific format), so digit structure alone
/// is never sufficient evidence -- an explicit account/bank context keyword is a hard
/// requirement for every candidate, not just a confidence modifier. No context at all means
/// no detection, for both a plain digit run and a hyphenated one. See AddFullCandidate.
///
/// Always Level3 when detected. RiskLevel and Confidence stay independent: an explicit
/// account-specific keyword ("계좌", "예금주", ...) yields High; the weaker, more generic
/// "은행" mention alone yields Medium.
///
/// Two match shapes:
///   - Full: 10-14 digits total, either one continuous run or 2-4 groups of 2-8 digits
///     separated by a single hyphen/space (per-group cap is just headroom for shapes like
///     "123-45-1234567"; the real 10-14 bound is enforced in code, not by the group cap).
///   - Masked: same grouped shape, but at least one group is real digits and at least one
///     group is asterisks (e.g. "123-***-789012") -- only the strong keyword tier accepts a
///     masked candidate, since masking already removed the little structural certainty a bare
///     digit-length check provided.
///
/// Guards against relabeling a more specific detector's match: before accepting any
/// candidate, this detector runs Phone/ResidentRegistrationNumber/CardNumber on the same text
/// and discards any account candidate that overlaps one of their matches, unconditionally --
/// e.g. a phone number sitting next to the word "계좌" must never become a bank account
/// candidate. Neither those detectors nor OverlapResolver are modified for this.
/// </summary>
public sealed class BankAccountNumberDetector : IDetector
{
    public string Name => "BankAccountNumberDetector";
    public PiiType PiiType => global::Privon.Detection.PiiType.BankAccountNumber;

    private const int MinDigits = 10;
    private const int MaxDigits = 14;

    // Per-group size allows up to 8 chars (e.g. the "123-45-*******" shape has a 7-char final
    // group) -- the real 10-14 total bound is enforced separately in code, not by this cap.
    private static readonly Regex ContinuousPattern = new(@"\d{10,14}", RegexOptions.Compiled);
    private static readonly Regex GroupedPattern = new(@"\d{2,8}(?:[-\s]\d{2,8}){1,3}", RegexOptions.Compiled);
    private static readonly Regex MaskedGroupedPattern = new(
        @"(?:\d{2,8}|\*{2,8})(?:[-\s](?:\d{2,8}|\*{2,8})){1,3}", RegexOptions.Compiled);

    // "계좌번호"/"계좌 번호"/"입금계좌"/"입금 계좌"/"송금계좌"/"송금 계좌" all contain "계좌" as
    // a contiguous substring, so checking for "계좌" alone already covers every one of them.
    private static readonly string[] StrongContextKeywords = ["계좌", "예금주"];
    private static readonly string[] WeakContextKeywords = ["은행"];
    private const int ContextWindowChars = 15;

    private readonly PhoneDetector _phoneGuard = new();
    private readonly ResidentRegistrationNumberDetector _rrnGuard = new();
    private readonly CardNumberDetector _cardGuard = new();

    public IReadOnlyList<DetectionCandidate> Detect(DetectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var otherDetectorSpans = _phoneGuard.Detect(context).Select(c => c.Span)
            .Concat(_rrnGuard.Detect(context).Select(c => c.Span))
            .Concat(_cardGuard.Detect(context).Select(c => c.Span))
            .ToList();

        var results = new List<DetectionCandidate>();

        foreach (Match m in ContinuousPattern.Matches(context.View.Text))
        {
            AddFullCandidate(context, m, otherDetectorSpans, results);
        }
        foreach (Match m in GroupedPattern.Matches(context.View.Text))
        {
            AddFullCandidate(context, m, otherDetectorSpans, results);
        }
        foreach (Match m in MaskedGroupedPattern.Matches(context.View.Text))
        {
            AddMaskedCandidate(context, m, otherDetectorSpans, results);
        }

        return results;
    }

    private void AddFullCandidate(DetectionContext context, Match m, List<RawSpan> otherDetectorSpans, List<DetectionCandidate> results)
    {
        var digitsOnly = BankAccountNumberCanonicalizer.StripSeparators(m.Value);
        if (digitsOnly.Length < MinDigits || digitsOnly.Length > MaxDigits) return;

        if (!HasCleanBoundary(context.View.Text, m)) return;

        var rawSpan = context.View.IndexMap.ToRawSpan(m.Index, m.Length);
        if (OverlapsAny(rawSpan, otherDetectorSpans)) return;

        var confidence = ContextConfidence(context.View.Text, m);
        if (confidence is null) return; // no context signal at all -- never detected.

        AddCandidate(rawSpan, m.Value, confidence.Value, results);
    }

    private void AddMaskedCandidate(DetectionContext context, Match m, List<RawSpan> otherDetectorSpans, List<DetectionCandidate> results)
    {
        var groups = m.Value.Split(['-', ' '], StringSplitOptions.RemoveEmptyEntries);
        bool allDigits = groups.All(g => g[0] != '*');
        bool allMasked = groups.All(g => g[0] == '*');
        // All-digit is the plain grouped pattern's job (avoids double-detecting the same
        // span). All-masked carries no digit evidence at all.
        if (allDigits || allMasked) return;

        var digitsOnly = BankAccountNumberCanonicalizer.StripSeparators(m.Value);
        if (digitsOnly.Length < MinDigits || digitsOnly.Length > MaxDigits) return;

        if (!HasCleanBoundary(context.View.Text, m)) return;

        var rawSpan = context.View.IndexMap.ToRawSpan(m.Index, m.Length);
        if (OverlapsAny(rawSpan, otherDetectorSpans)) return;

        if (!HasKeyword(context.View.Text, m, StrongContextKeywords)) return;

        AddCandidate(rawSpan, m.Value, DetectionConfidence.High, results);
    }

    private void AddCandidate(RawSpan rawSpan, string matchedText, DetectionConfidence confidence, List<DetectionCandidate> results)
    {
        var canonical = BankAccountNumberCanonicalizer.Canonicalize(matchedText);
        results.Add(new DetectionCandidate(
            global::Privon.Detection.PiiType.BankAccountNumber,
            rawSpan,
            RiskLevel.Level3,
            confidence,
            canonical,
            Name));
    }

    private static DetectionConfidence? ContextConfidence(string normalizedText, Match m)
    {
        if (HasKeyword(normalizedText, m, StrongContextKeywords)) return DetectionConfidence.High;
        if (HasKeyword(normalizedText, m, WeakContextKeywords)) return DetectionConfidence.Medium;
        return null;
    }

    private static bool HasKeyword(string normalizedText, Match m, string[] keywords)
    {
        int windowStart = Math.Max(0, m.Index - ContextWindowChars);
        int windowEnd = Math.Min(normalizedText.Length, m.Index + m.Length + ContextWindowChars);
        var window = normalizedText[windowStart..windowEnd];

        foreach (var keyword in keywords)
        {
            if (window.Contains(keyword, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static bool OverlapsAny(RawSpan span, List<RawSpan> others)
    {
        foreach (var other in others)
        {
            if (span.OverlapsWith(other)) return true;
        }
        return false;
    }

    // Boundary hardening, same principle as Email/RRN/CardNumber's HasCleanBoundary.
    private static bool HasCleanBoundary(string normalizedText, Match m)
    {
        if (m.Index > 0 && IsDigitOrMask(normalizedText[m.Index - 1])) return false;

        int endIndex = m.Index + m.Length;
        if (endIndex < normalizedText.Length && IsDigitOrMask(normalizedText[endIndex])) return false;

        return true;
    }

    private static bool IsDigitOrMask(char c) => char.IsAsciiDigit(c) || c == '*';
}
