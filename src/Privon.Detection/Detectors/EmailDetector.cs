using System.Text.RegularExpressions;
using Privon.Core;

namespace Privon.Detection.Detectors;

/// <summary>
/// Deterministic email detector. Two independent matching passes, kept separate rather than
/// merged into one regex:
///   - Standard: ordinary "local@domain.tld" syntax -- Level 2, High confidence
///     (design doc decision 56).
///   - Obfuscated: "(at)"/"[at]" and/or "(dot)" stand-ins for '@'/'.' -- Level 2, Medium
///     confidence, since the match relies on textual context rather than exact email syntax
///     (design doc decision 172).
/// Deliberately conservative, not a full RFC 5322 grammar: local-part and domain labels
/// reject leading/trailing/consecutive dots, and the domain must end in a dot-separated,
/// letters-only TLD of at least two characters. Whether an obfuscated or example/test-context
/// match ultimately gets acted on is a Policy-layer decision in a later phase -- this detector
/// only reports RiskLevel/Confidence, it never suppresses a structurally valid match.
/// </summary>
public sealed class EmailDetector : IDetector
{
    public string Name => "EmailDetector";
    public PiiType PiiType => global::Privon.Detection.PiiType.Email;

    private const string LocalPart = @"[A-Za-z0-9_+-]+(?:\.[A-Za-z0-9_+-]+)*";
    private const string DomainLabel = @"[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?";
    private const string Tld = @"[A-Za-z]{2,}";
    private const string PlainDot = @"\.";
    private const string DotOrMarker = @"\s*(?:\.|\(\s*dot\s*\))\s*";
    private const string AtWordMarker = @"(?:\(\s*at\s*\)|\[\s*at\s*\])";

    // "user@example.com" / "first.last@example.com" / "user+tag@example.co.kr" -- literal
    // '@', plain-dot-separated domain only.
    private static readonly Regex StandardPattern = new(
        $@"(?<local>{LocalPart})@(?<domain>{DomainLabel}(?:{PlainDot}{DomainLabel})*{PlainDot}{Tld})",
        RegexOptions.Compiled);

    // "user(at)example.com" / "user [at] example (dot) com" -- (at)/[at] marker instead of
    // '@'; domain separators may be plain dots or "(dot)" markers, freely mixed.
    private static readonly Regex ObfuscatedAtPattern = new(
        $@"(?<local>{LocalPart})\s*{AtWordMarker}\s*(?<domain>{DomainLabel}(?:{DotOrMarker}{DomainLabel})*{DotOrMarker}{Tld})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // "user@example(dot)com" -- literal '@', but domain uses at least one "(dot)" marker.
    // Same shape as StandardPattern with DotOrMarker instead of PlainDot; HasObfuscatedDotMarker
    // below rejects matches that turn out fully plain, since those are already covered by
    // StandardPattern and must not be double-detected.
    private static readonly Regex ObfuscatedDotPattern = new(
        $@"(?<local>{LocalPart})@(?<domain>{DomainLabel}(?:{DotOrMarker}{DomainLabel})*{DotOrMarker}{Tld})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex HasObfuscatedDotMarker = new(
        @"\(\s*dot\s*\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Boundary hardening (Phase 2B.1): a match is only accepted if the character immediately
    // outside it could not itself have extended the local-part/domain grammar. Without this,
    // a malformed token like "user..name@example.com" lets the regex give up on "user.."
    // and restart cleanly at "name@example.com", silently dropping the identifying "user"
    // prefix while still reporting a "protected" match. This never widens what the regex
    // matches -- it only rejects candidates whose surroundings prove they are a fragment of
    // a larger, invalid token rather than a genuinely bounded address.
    //
    // The two sides are asymmetric on purpose:
    //   - Preceding local-part-adjacent chars ('.' included) always indicate a fragment,
    //     because LocalPart's own grammar would have consumed a continuing '.'+chars run if
    //     it had been well-formed -- the only way one is left dangling just before a
    //     successful match is a prior break (e.g. "..").
    //   - Following domain-adjacent chars deliberately exclude '.' and letters: Tld is
    //     greedy and terminal, so it always consumes every contiguous letter already (nothing
    //     letter-shaped can ever dangle), and a trailing '.' is ordinary sentence punctuation
    //     (domain already backtracks to swallow real multi-label domains like "example.co.kr"
    //     -- see DomainLabel's optional loop). Only a dangling digit/hyphen/'@' proves the
    //     domain grammar was cut short.
    private static bool HasCleanBoundary(string normalizedText, Match m)
    {
        if (m.Index > 0 && IsLocalPartAdjacent(normalizedText[m.Index - 1])) return false;

        int endIndex = m.Index + m.Length;
        if (endIndex < normalizedText.Length && IsDomainAdjacent(normalizedText[endIndex])) return false;

        return true;
    }

    private static bool IsLocalPartAdjacent(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '_' or '+' or '-' or '.' or '@';

    private static bool IsDomainAdjacent(char c) =>
        char.IsAsciiDigit(c) || c is '-' or '@';

    public IReadOnlyList<DetectionCandidate> Detect(DetectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var results = new List<DetectionCandidate>();
        DetectStandard(context, results);
        DetectObfuscated(context, results);
        return results;
    }

    private void DetectStandard(DetectionContext context, List<DetectionCandidate> results)
    {
        foreach (Match m in StandardPattern.Matches(context.View.Text))
        {
            AddCandidate(context, m, DetectionConfidence.High, results);
        }
    }

    private void DetectObfuscated(DetectionContext context, List<DetectionCandidate> results)
    {
        foreach (Match m in ObfuscatedAtPattern.Matches(context.View.Text))
        {
            AddCandidate(context, m, DetectionConfidence.Medium, results);
        }

        foreach (Match m in ObfuscatedDotPattern.Matches(context.View.Text))
        {
            if (!HasObfuscatedDotMarker.IsMatch(m.Groups["domain"].Value)) continue;
            AddCandidate(context, m, DetectionConfidence.Medium, results);
        }
    }

    private void AddCandidate(DetectionContext context, Match m, DetectionConfidence confidence, List<DetectionCandidate> results)
    {
        if (!HasCleanBoundary(context.View.Text, m)) return;

        var rawSpan = context.View.IndexMap.ToRawSpan(m.Index, m.Length);
        var canonical = EmailCanonicalizer.Canonicalize(m);
        results.Add(new DetectionCandidate(
            global::Privon.Detection.PiiType.Email,
            rawSpan,
            RiskLevel.Level2,
            confidence,
            canonical,
            Name));
    }
}
