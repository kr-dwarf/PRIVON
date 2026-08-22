using System.Text.RegularExpressions;
using Privon.Core;

namespace Privon.Detection.Detectors;

/// <summary>
/// Credential/secret detector: assignment-style ("api_key=...", "token: ...") and
/// Authorization-Bearer-style values. Always Level3 when detected. RiskLevel and Confidence
/// stay independent.
///
/// Deliberately NOT a single giant regex -- two independently-scoped match passes:
///   1. Assignment-style (<see cref="AssignmentPattern"/>): a fixed, explicitly-enumerated
///      key-name alternation (no invented vendor prefixes) followed by an operator and a
///      value. Only the VALUE (not the key name, not surrounding quotes) becomes the
///      candidate span -- see AddCandidate's use of the dq/sq/uq named groups' own Index.
///   2. Bearer-style (<see cref="BearerPattern"/>): optional "Authorization:" prefix, then
///      "Bearer" plus a token value. The bare word "Bearer" alone, with no value clearing
///      minimum evidence, is never a candidate.
///
/// "Looks long/random" is never sufficient evidence on its own for either pattern -- a
/// recognized key name (or "Bearer") is a hard prerequisite, and even then the value must
/// clear <see cref="MeetsMinimumEvidence"/> (length + character-class diversity, with a
/// narrow numeric-only exception for strong keys -- see Phase 2F.1 -- + a small denylist of
/// placeholder words like "true"/"test"/"none"). A fully-masked value (no visible character
/// evidence at all, e.g. "********") still fails by construction (all-symbol, not
/// all-digit) and is deliberately never a candidate -- see SecretDetectorTests for the test
/// that locks this decision in.
///
/// Structured-secret patterns (e.g. PEM private-key blocks) are explicitly OUT of scope for
/// this phase -- see the class's OPEN_QUESTION note in the Phase 2F report, not implemented
/// here to avoid scope creep.
///
/// This detector does NOT call into Phone/RRN/CardNumber/BankAccountNumber to guard against
/// them the way BankAccountNumberDetector does -- per the Phase 2E architecture note, that
/// direct-guard composition pattern is not extended to further detectors. Cross-detector
/// conflicts here are left to the future central Pipeline/Overlap/Policy layer.
///
/// Phase 2F.1 -- Fail-Closed Hardening: two deliberate ambiguity-resolution changes from the
/// original Phase 2F cut, both erring toward over-protecting rather than under-protecting:
///   - No more trailing-'.'-trimming on unquoted values (see AddAssignmentCandidate /
///     AddBearerCandidate) -- a detector can't tell "sentence punctuation" from "the secret's
///     last character actually is a dot", so it no longer guesses; the whole matched run is
///     kept.
///   - CanonicalValue is built from the RAW text at the resolved RawSpan (see AddCandidate),
///     not from the matched/normalized text -- because NormalizedView strips zero-width
///     characters before detectors ever see it, and a zero-width character inside a secret
///     might be a real credential character rather than noise (unlike Phone/Email/RRN, where
///     it's unambiguously an evasion artifact around a human-readable number).
/// </summary>
public sealed class SecretDetector : IDetector
{
    public string Name => "SecretDetector";
    public PiiType PiiType => global::Privon.Detection.PiiType.Secret;

    // Deliberately literal, explicitly-enumerated key names -- no vendor-prefix guessing, no
    // generalized "any word + separator" leniency beyond what's listed here.
    private const string KeyAlternation =
        "api_key|apikey|api-key|api key|" +
        "token|access_token|access-token|access token|" +
        "secret|client_secret|client-secret|client secret|" +
        "password|passwd|" +
        "auth_token|authorization_token|" +
        "private_key";

    // Value characters: a conservative, ASCII-only "credential-safe" set (covers
    // base64/URL-safe/JWT-shaped tokens). Deliberately excludes non-ASCII text, whitespace,
    // and structural punctuation (,;)]}) so an unquoted value never bleeds into surrounding
    // prose -- e.g. Korean sentence text immediately after the value with no space.
    private const string UnquotedValueChars = @"[A-Za-z0-9+/=_.\-~!@#$%^&*]+";

    private static readonly Regex AssignmentPattern = new(
        @"(?<key>" + KeyAlternation + @")\s*(?::=|=|:)\s*" +
        @"(?:""(?<dq>[^""]*)""|'(?<sq>[^']*)'|(?<uq>" + UnquotedValueChars + "))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BearerPattern = new(
        @"(?<prefix>authorization\s*:\s*)?bearer\s+(?<token>" + UnquotedValueChars + ")",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> StrongKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "api_key", "apikey", "api-key", "api key",
        "secret", "client_secret", "client-secret", "client secret",
        "password", "passwd", "private_key",
    };

    private const int StrongKeyMinLength = 6;
    private const int GeneralKeyMinLength = 10;
    private const int BearerMinLength = 16;

    // Phase 2F.1: a strong-key value that is digits-only still needs to look deliberately
    // secret-shaped, not just "long enough" -- set comfortably above common short numeric
    // codes/PINs. Not derived from any external source; revisit once real corpus data exists
    // (see OPEN_QUESTION in the Phase 2F.1 report).
    private const int NumericOnlyStrongKeyMinLength = 12;

    private static readonly HashSet<string> TrivialValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "true", "false", "yes", "no", "none", "null", "test", "example",
        "n/a", "na", "xxx", "todo", "changeme", "password", "secret", "token", "value",
    };

    // Matches the codebase's existing Korean alias-token naming convention
    // ([전화번호1], [이메일1], [주민등록번호1], [카드번호1], [계좌번호1]).
    private const string AlreadyProtectedAliasToken = "[시크릿1]";

    public IReadOnlyList<DetectionCandidate> Detect(DetectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var results = new List<DetectionCandidate>();
        foreach (Match m in AssignmentPattern.Matches(context.View.Text))
        {
            AddAssignmentCandidate(context, m, results);
        }
        foreach (Match m in BearerPattern.Matches(context.View.Text))
        {
            AddBearerCandidate(context, m, results);
        }
        return results;
    }

    private void AddAssignmentCandidate(DetectionContext context, Match m, List<DetectionCandidate> results)
    {
        if (!HasCleanKeyBoundary(context.View.Text, m.Groups["key"].Index)) return;

        var valueGroup = ExtractValueGroup(m);
        if (!valueGroup.Success) return;
        if (valueGroup.Value == AlreadyProtectedAliasToken) return;

        var isStrongKey = StrongKeys.Contains(m.Groups["key"].Value);
        var minLength = isStrongKey ? StrongKeyMinLength : GeneralKeyMinLength;
        if (!MeetsMinimumEvidence(valueGroup.Value, minLength, allowNumericOnly: isStrongKey)) return;

        var rawSpan = context.View.IndexMap.ToRawSpan(valueGroup.Index, valueGroup.Length);
        var confidence = isStrongKey ? DetectionConfidence.High : DetectionConfidence.Medium;
        AddCandidate(context, rawSpan, confidence, results);
    }

    private void AddBearerCandidate(DetectionContext context, Match m, List<DetectionCandidate> results)
    {
        if (!HasCleanKeyBoundary(context.View.Text, m.Index)) return;

        var tokenGroup = m.Groups["token"];
        if (tokenGroup.Value == AlreadyProtectedAliasToken) return;
        if (!MeetsMinimumEvidence(tokenGroup.Value, BearerMinLength, allowNumericOnly: false)) return;

        var rawSpan = context.View.IndexMap.ToRawSpan(tokenGroup.Index, tokenGroup.Length);
        AddCandidate(context, rawSpan, DetectionConfidence.High, results);
    }

    private void AddCandidate(DetectionContext context, RawSpan rawSpan, DetectionConfidence confidence, List<DetectionCandidate> results)
    {
        // Phase 2F.1: canonicalize from the RAW characters at this span, not the matched
        // (normalized) text -- see class doc. A zero-width character embedded in the raw
        // value must not be silently dropped just because normalization stripped it for
        // detection purposes.
        var rawValue = context.View.RawText.Substring(rawSpan.Start, rawSpan.Length);
        var canonical = SecretCanonicalizer.Canonicalize(rawValue);
        results.Add(new DetectionCandidate(
            global::Privon.Detection.PiiType.Secret,
            rawSpan,
            RiskLevel.Level3,
            confidence,
            canonical,
            Name));
    }

    private static Group ExtractValueGroup(Match m)
    {
        if (m.Groups["dq"].Success) return m.Groups["dq"];
        if (m.Groups["sq"].Success) return m.Groups["sq"];
        return m.Groups["uq"];
    }

    private static bool MeetsMinimumEvidence(string value, int minLength, bool allowNumericOnly)
    {
        if (string.IsNullOrEmpty(value)) return false;
        if (value.Length < minLength) return false;
        if (TrivialValues.Contains(value)) return false;

        bool hasLower = false, hasUpper = false, hasDigit = false, hasSymbol = false;
        foreach (var c in value)
        {
            if (c is >= 'a' and <= 'z') hasLower = true;
            else if (c is >= 'A' and <= 'Z') hasUpper = true;
            else if (c is >= '0' and <= '9') hasDigit = true;
            else hasSymbol = true;
        }
        int classes = (hasLower ? 1 : 0) + (hasUpper ? 1 : 0) + (hasDigit ? 1 : 0) + (hasSymbol ? 1 : 0);
        if (classes >= 2) return true;

        // Phase 2F.1: a sufficiently long, purely-numeric value under an explicit strong-key
        // context (password=, client_secret=, api_key=, ...) is still accepted even though it
        // fails the general diversity requirement -- e.g. numeric PINs/OTP-style secrets.
        // Deliberately does NOT extend to all-symbol values (a fully-masked "********" still
        // has hasSymbol=true/hasDigit=false and stays rejected) or to general/Bearer keys.
        if (allowNumericOnly && hasDigit && !hasLower && !hasUpper && !hasSymbol
            && value.Length >= NumericOnlyStrongKeyMinLength)
        {
            return true;
        }

        return false;
    }

    // Boundary hardening: the key/Bearer match must not be a suffix of a longer identifier
    // (e.g. "myapi_key=value" must not be read as the recognized key "api_key"). The trailing
    // side needs no equivalent check -- the operator/whitespace requirement immediately after
    // the key already forces the regex to fail rather than match a key-shaped prefix of a
    // longer identifier like "tokenizer=xyz".
    private static bool HasCleanKeyBoundary(string normalizedText, int matchStartIndex)
    {
        if (matchStartIndex == 0) return true;
        char before = normalizedText[matchStartIndex - 1];
        return !(char.IsAsciiLetterOrDigit(before) || before is '_' or '-');
    }
}
