using Privon.Core;
using Privon.Detection;

namespace Privon.Detection.Tests;

// Phase 2U.1 — AliasReplacer tests. All fixtures are synthetic filler, never real PII. Spans
// deliberately point at arbitrary placeholder substrings (e.g. "PHONE") rather than real-shaped
// phone numbers where the exact content doesn't matter -- AliasReplacer never re-detects or
// re-validates PII shape, it only trusts RawSpan positions and alias tokens.
public class AliasReplacerTests
{
    private static DetectionCandidate DetCandidate(PiiType type, int start, int length, string canonical = "V") =>
        new(type, new RawSpan(start, length), RiskLevel.Level2, DetectionConfidence.High, new CanonicalValue(type, canonical), "Test");

    private static CandidatePolicyDecision Decision(PiiType type, int start, int length, CandidateDisposition disposition, TrustState trust = TrustState.Untrusted, string canonical = "V") =>
        new(new EvaluatedCandidate(DetCandidate(type, start, length, canonical), trust), disposition);

    private static AliasToken ValidToken(PiiType type, int number) =>
        new(type, number, $"[{AliasLabelProvider.GetLabel(type)}{number}]");

    private static AliasAssignment Protect(PiiType type, int start, int length, int number, string canonical = "V") =>
        new(Decision(type, start, length, CandidateDisposition.Protect, canonical: canonical), ValidToken(type, number));

    private static AliasAssignment Bypass(PiiType type, int start, int length) =>
        new(Decision(type, start, length, CandidateDisposition.Bypass, TrustState.Trusted), null);

    private static AliasAssignment NeedsDecision(PiiType type, int start, int length) =>
        new(Decision(type, start, length, CandidateDisposition.NeedsDecision), null);

    // ---- 1. single Phone replacement ----
    [Fact]
    public void SinglePhoneReplacement()
    {
        const string raw = "전화:010-1234-5678!";
        var assignment = Protect(PiiType.Phone, start: 3, length: 13, number: 1);

        var result = AliasReplacer.Apply(raw, [assignment]);

        Assert.Equal("전화:[전화번호1]!", result);
    }

    // ---- 2. single Email replacement ----
    [Fact]
    public void SingleEmailReplacement()
    {
        const string raw = "메일:user@example.com!";
        var assignment = Protect(PiiType.Email, start: 3, length: 16, number: 1);

        var result = AliasReplacer.Apply(raw, [assignment]);

        Assert.Equal("메일:[이메일1]!", result);
    }

    // ---- 3. multiple replacements, descending mutation still produces the correct final string ----
    [Fact]
    public void MultipleReplacements_DescendingMutation_CorrectResult()
    {
        const string raw = "AAA PHONE BBB EMAIL CCC";
        var phone = Protect(PiiType.Phone, start: 4, length: 5, number: 1);
        var email = Protect(PiiType.Email, start: 14, length: 5, number: 1);

        var result = AliasReplacer.Apply(raw, [phone, email]);

        Assert.Equal("AAA [전화번호1] BBB [이메일1] CCC", result);
    }

    // ---- 4. replacement token shorter than the raw span ----
    [Fact]
    public void ReplacementToken_ShorterThanRawSpan()
    {
        const string raw = "X:AAAAAAAAAA:Y";
        var assignment = Protect(PiiType.Phone, start: 2, length: 10, number: 1); // "[전화번호1]" is shorter than 10 chars

        var result = AliasReplacer.Apply(raw, [assignment]);

        Assert.Equal("X:[전화번호1]:Y", result);
    }

    // ---- 5. replacement token longer than the raw span ----
    [Fact]
    public void ReplacementToken_LongerThanRawSpan()
    {
        const string raw = "X:A:Y";
        var assignment = Protect(PiiType.Phone, start: 2, length: 1, number: 1); // "[전화번호1]" is longer than 1 char

        var result = AliasReplacer.Apply(raw, [assignment]);

        Assert.Equal("X:[전화번호1]:Y", result);
    }

    // ---- 6/7/8. only Protect mutates; Bypass and NeedsDecision regions stay untouched ----
    [Fact]
    public void OnlyProtectMutates_BypassAndNeedsDecisionUnchanged()
    {
        const string raw = "P:PHONE|B:BYPASS|N:NEEDS";
        var protect = Protect(PiiType.Phone, start: 2, length: 5, number: 1);   // "PHONE"
        var bypass = Bypass(PiiType.Email, start: 10, length: 6);               // "BYPASS"
        var needs = NeedsDecision(PiiType.GpsCoordinate, start: 19, length: 5); // "NEEDS"

        var result = AliasReplacer.Apply(raw, [protect, bypass, needs]);

        Assert.Equal("P:[전화번호1]|B:BYPASS|N:NEEDS", result);
    }

    // ---- 9/10/11. disposition/alias consistency violations ----
    [Fact]
    public void BypassWithNonNullAlias_Throws()
    {
        var malformed = new AliasAssignment(Decision(PiiType.Phone, 0, 5, CandidateDisposition.Bypass, TrustState.Trusted), ValidToken(PiiType.Phone, 1));
        Assert.Throws<ArgumentException>(() => AliasReplacer.Apply("AAAAA", [malformed]));
    }

    [Fact]
    public void NeedsDecisionWithNonNullAlias_Throws()
    {
        var malformed = new AliasAssignment(Decision(PiiType.Phone, 0, 5, CandidateDisposition.NeedsDecision), ValidToken(PiiType.Phone, 1));
        Assert.Throws<ArgumentException>(() => AliasReplacer.Apply("AAAAA", [malformed]));
    }

    [Fact]
    public void ProtectWithNullAlias_Throws()
    {
        var malformed = new AliasAssignment(Decision(PiiType.Phone, 0, 5, CandidateDisposition.Protect), null);
        Assert.Throws<ArgumentException>(() => AliasReplacer.Apply("AAAAA", [malformed]));
    }

    // ---- 12. candidate/alias PiiType mismatch ----
    [Fact]
    public void PiiTypeMismatch_Throws()
    {
        var malformed = new AliasAssignment(Decision(PiiType.Phone, 0, 5, CandidateDisposition.Protect), ValidToken(PiiType.Email, 1));
        Assert.Throws<ArgumentException>(() => AliasReplacer.Apply("AAAAA", [malformed]));
    }

    // ---- 13/14. AliasToken.Number invalid ----
    [Fact]
    public void AliasNumberZero_Throws()
    {
        var malformed = new AliasAssignment(Decision(PiiType.Phone, 0, 5, CandidateDisposition.Protect), new AliasToken(PiiType.Phone, 0, "[전화번호0]"));
        Assert.Throws<ArgumentException>(() => AliasReplacer.Apply("AAAAA", [malformed]));
    }

    [Fact]
    public void AliasNumberNegative_Throws()
    {
        var malformed = new AliasAssignment(Decision(PiiType.Phone, 0, 5, CandidateDisposition.Protect), new AliasToken(PiiType.Phone, -1, "[전화번호-1]"));
        Assert.Throws<ArgumentException>(() => AliasReplacer.Apply("AAAAA", [malformed]));
    }

    // ---- 15/16. AliasToken.Value malformed / wrong number text ----
    [Fact]
    public void AliasValueMalformed_Throws()
    {
        var malformed = new AliasAssignment(Decision(PiiType.Phone, 0, 5, CandidateDisposition.Protect), new AliasToken(PiiType.Phone, 1, "010-1234-5678"));
        Assert.Throws<ArgumentException>(() => AliasReplacer.Apply("AAAAA", [malformed]));
    }

    [Fact]
    public void AliasValueWrongNumber_Throws()
    {
        var malformed = new AliasAssignment(Decision(PiiType.Phone, 0, 5, CandidateDisposition.Protect), new AliasToken(PiiType.Phone, 1, "[전화번호2]"));
        Assert.Throws<ArgumentException>(() => AliasReplacer.Apply("AAAAA", [malformed]));
    }

    // ---- 17. runtime-null AliasToken.Value ----
    [Fact]
    public void AliasValueNull_Throws()
    {
        var malformed = new AliasAssignment(Decision(PiiType.Phone, 0, 5, CandidateDisposition.Protect), new AliasToken(PiiType.Phone, 1, null!));
        Assert.Throws<ArgumentException>(() => AliasReplacer.Apply("AAAAA", [malformed]));
    }

    // ---- 18. invalid PiiType on the alias token ----
    [Fact]
    public void InvalidAliasPiiType_Throws()
    {
        var malformed = new AliasAssignment(
            new CandidatePolicyDecision(new EvaluatedCandidate(new DetectionCandidate((PiiType)999, new RawSpan(0, 5), RiskLevel.Level2, DetectionConfidence.High, new CanonicalValue((PiiType)999, "V"), "Test"), TrustState.Untrusted), CandidateDisposition.Protect),
            new AliasToken((PiiType)999, 1, "[??1]"));
        Assert.Throws<ArgumentOutOfRangeException>(() => AliasReplacer.Apply("AAAAA", [malformed]));
    }

    // ---- 19. invalid CandidateDisposition ----
    [Fact]
    public void InvalidCandidateDisposition_Throws()
    {
        var malformed = new AliasAssignment(Decision(PiiType.Phone, 0, 5, (CandidateDisposition)999), null);
        Assert.Throws<ArgumentOutOfRangeException>(() => AliasReplacer.Apply("AAAAA", [malformed]));
    }

    // ---- 20-25. span bounds ----
    [Fact]
    public void NegativeStart_Throws()
    {
        var malformed = new AliasAssignment(Decision(PiiType.Phone, -1, 5, CandidateDisposition.Protect), ValidToken(PiiType.Phone, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => AliasReplacer.Apply("AAAAA", [malformed]));
    }

    [Fact]
    public void ZeroLength_Throws()
    {
        var malformed = new AliasAssignment(Decision(PiiType.Phone, 0, 0, CandidateDisposition.Protect), ValidToken(PiiType.Phone, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => AliasReplacer.Apply("AAAAA", [malformed]));
    }

    [Fact]
    public void NegativeLength_Throws()
    {
        var malformed = new AliasAssignment(Decision(PiiType.Phone, 0, -3, CandidateDisposition.Protect), ValidToken(PiiType.Phone, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => AliasReplacer.Apply("AAAAA", [malformed]));
    }

    [Fact]
    public void StartBeyondEnd_Throws()
    {
        var malformed = new AliasAssignment(Decision(PiiType.Phone, 100, 5, CandidateDisposition.Protect), ValidToken(PiiType.Phone, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => AliasReplacer.Apply("AAAAA", [malformed]));
    }

    [Fact]
    public void LengthBeyondRemainingText_Throws()
    {
        var malformed = new AliasAssignment(Decision(PiiType.Phone, 3, 10, CandidateDisposition.Protect), ValidToken(PiiType.Phone, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => AliasReplacer.Apply("AAAAA", [malformed]));
    }

    // ---- 25 (extreme). huge int span values must fail safely, not overflow/wrap ----
    [Fact]
    public void ExtremeSpanValues_FailSafely_NoOverflow()
    {
        var hugeStart = new AliasAssignment(Decision(PiiType.Phone, int.MaxValue - 2, 5, CandidateDisposition.Protect), ValidToken(PiiType.Phone, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => AliasReplacer.Apply("AAAAA", [hugeStart]));

        var hugeLength = new AliasAssignment(Decision(PiiType.Phone, 0, int.MaxValue - 2, CandidateDisposition.Protect), ValidToken(PiiType.Phone, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => AliasReplacer.Apply("AAAAA", [hugeLength]));
    }

    // ---- 26/27/28. overlap / duplicate / nested spans ----
    [Fact]
    public void OverlappingSpans_Throw()
    {
        var a = Protect(PiiType.Phone, start: 0, length: 10, number: 1);
        var b = Protect(PiiType.Email, start: 5, length: 10, number: 1);
        Assert.Throws<ArgumentException>(() => AliasReplacer.Apply(new string('A', 20), [a, b]));
    }

    [Fact]
    public void DuplicateSpans_Throw()
    {
        var a = Protect(PiiType.Phone, start: 0, length: 10, number: 1);
        var b = Protect(PiiType.Email, start: 0, length: 10, number: 1);
        Assert.Throws<ArgumentException>(() => AliasReplacer.Apply(new string('A', 20), [a, b]));
    }

    [Fact]
    public void NestedSpans_Throw()
    {
        var outer = Protect(PiiType.Phone, start: 0, length: 20, number: 1);
        var inner = Protect(PiiType.Email, start: 5, length: 5, number: 1);
        Assert.Throws<ArgumentException>(() => AliasReplacer.Apply(new string('A', 20), [outer, inner]));
    }

    // ---- 29/30. same alias token / same canonical, different non-overlapping spans -> success ----
    [Fact]
    public void SameAliasOnSeparateSpans_Success()
    {
        const string raw = "A:PHONE1:B:PHONE2:C";
        var first = Protect(PiiType.Phone, start: 2, length: 6, number: 1, canonical: "SAME");
        var second = Protect(PiiType.Phone, start: 11, length: 6, number: 1, canonical: "SAME");

        var result = AliasReplacer.Apply(raw, [first, second]);

        Assert.Equal("A:[전화번호1]:B:[전화번호1]:C", result);
    }

    // ---- 31/32/33. no-op cases: output equals raw input unchanged ----
    [Fact]
    public void EmptyAssignments_RawUnchanged()
    {
        const string raw = "아무 개인정보도 없는 문장입니다.";
        Assert.Equal(raw, AliasReplacer.Apply(raw, []));
    }

    [Fact]
    public void AllBypass_RawUnchanged()
    {
        const string raw = "AAAAABBBBB";
        var result = AliasReplacer.Apply(raw, [Bypass(PiiType.Phone, 0, 5), Bypass(PiiType.Email, 5, 5)]);
        Assert.Equal(raw, result);
    }

    [Fact]
    public void AllNeedsDecision_RawUnchanged()
    {
        const string raw = "AAAAABBBBB";
        var result = AliasReplacer.Apply(raw, [NeedsDecision(PiiType.Phone, 0, 5), NeedsDecision(PiiType.Email, 5, 5)]);
        Assert.Equal(raw, result);
    }

    // ---- 34/35/36. null inputs fail fast ----
    [Fact]
    public void NullRawText_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AliasReplacer.Apply(null!, []));
    }

    [Fact]
    public void NullAssignments_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AliasReplacer.Apply("text", null!));
    }

    [Fact]
    public void NullAssignmentItem_Throws()
    {
        var list = new List<AliasAssignment> { null! };
        Assert.Throws<ArgumentException>(() => AliasReplacer.Apply("text", list));
    }

    // ---- 37. punctuation immediately outside the span is preserved ----
    [Fact]
    public void PunctuationOutsideSpan_Preserved()
    {
        const string raw = "전화:010-1234-5678!";
        var result = AliasReplacer.Apply(raw, [Protect(PiiType.Phone, start: 3, length: 13, number: 1)]);
        Assert.StartsWith("전화:", result);
        Assert.EndsWith("!", result);
    }

    // ---- 38/39. zero-width immediately before/after the span is preserved ----
    [Fact]
    public void ZeroWidthBeforeSpan_Preserved()
    {
        var zw = ((char)0x200B).ToString();
        var raw = $"{zw}PHONE";
        var result = AliasReplacer.Apply(raw, [Protect(PiiType.Phone, start: 1, length: 5, number: 1)]);
        Assert.Equal($"{zw}[전화번호1]", result);
    }

    [Fact]
    public void ZeroWidthAfterSpan_Preserved()
    {
        var zw = ((char)0x200B).ToString();
        var raw = $"PHONE{zw}";
        var result = AliasReplacer.Apply(raw, [Protect(PiiType.Phone, start: 0, length: 5, number: 1)]);
        Assert.Equal($"[전화번호1]{zw}", result);
    }

    // ---- 40/41/42. emoji (surrogate pair) before / between / after spans ----
    [Fact]
    public void EmojiBeforeSpan_Correct()
    {
        const string raw = "😀 PHONE";
        var result = AliasReplacer.Apply(raw, [Protect(PiiType.Phone, start: 3, length: 5, number: 1)]);
        Assert.Equal("😀 [전화번호1]", result);
    }

    [Fact]
    public void EmojiBetweenSpans_Correct()
    {
        const string raw = "PHONE😀EMAIL";
        var phone = Protect(PiiType.Phone, start: 0, length: 5, number: 1);
        var email = Protect(PiiType.Email, start: 7, length: 5, number: 1); // 😀 is 2 chars (surrogate pair)

        var result = AliasReplacer.Apply(raw, [phone, email]);

        Assert.Equal("[전화번호1]😀[이메일1]", result);
    }

    [Fact]
    public void EmojiSuffix_Correct()
    {
        const string raw = "PHONE 😀";
        var result = AliasReplacer.Apply(raw, [Protect(PiiType.Phone, start: 0, length: 5, number: 1)]);
        Assert.Equal("[전화번호1] 😀", result);
    }

    // ---- 43/44. CRLF / LF multiline ----
    [Fact]
    public void CrLfMultiline_Correct()
    {
        const string raw = "Line1\r\nPHONE\r\nLine3";
        var result = AliasReplacer.Apply(raw, [Protect(PiiType.Phone, start: 7, length: 5, number: 1)]);
        Assert.Equal("Line1\r\n[전화번호1]\r\nLine3", result);
    }

    [Fact]
    public void LfMultiline_Correct()
    {
        const string raw = "Line1\nPHONE\nLine3";
        var result = AliasReplacer.Apply(raw, [Protect(PiiType.Phone, start: 6, length: 5, number: 1)]);
        Assert.Equal("Line1\n[전화번호1]\nLine3", result);
    }

    // ---- 45. Korean + emoji + phone + email mixed ----
    [Fact]
    public void KoreanEmojiPhoneEmailMixed_Correct()
    {
        const string raw = "안녕하세요😀 PHONE 그리고 EMAIL 입니다.";
        var phone = Protect(PiiType.Phone, start: 8, length: 5, number: 1);
        var emailStart = raw.IndexOf("EMAIL", StringComparison.Ordinal);
        var email = Protect(PiiType.Email, start: emailStart, length: 5, number: 1);

        var result = AliasReplacer.Apply(raw, [phone, email]);

        Assert.Equal("안녕하세요😀 [전화번호1] 그리고 [이메일1] 입니다.", result);
    }

    // ---- 46. existing user-written [전화번호1] text with no assignment is left untouched ----
    [Fact]
    public void ExistingUserWrittenAliasShapedText_Untouched()
    {
        const string raw = "이미 [전화번호1] 로 표시되어 있고, PHONE 도 있습니다.";
        var start = raw.IndexOf("PHONE", StringComparison.Ordinal);
        var result = AliasReplacer.Apply(raw, [Protect(PiiType.Phone, start, 5, 1)]);

        Assert.Contains("[전화번호1] 로 표시되어", result); // the pre-existing literal text, untouched
        Assert.Contains("[전화번호1] 도 있습니다", result); // the newly replaced value
    }

    // ---- 47. URL: only the precise value span is replaced ----
    [Fact]
    public void UrlSurroundingText_OnlyPreciseSpanReplaced()
    {
        const string raw = "https://example.com/?email=EMAILVALUE&ref=x";
        var start = raw.IndexOf("EMAILVALUE", StringComparison.Ordinal);
        var result = AliasReplacer.Apply(raw, [Protect(PiiType.Email, start, "EMAILVALUE".Length, 1)]);

        Assert.Equal("https://example.com/?email=[이메일1]&ref=x", result);
    }

    // ---- 48. filename: only the precise value span is replaced ----
    [Fact]
    public void FilenameSurroundingText_OnlyPreciseSpanReplaced()
    {
        const string raw = "customer_PHONEVALUE.txt";
        var start = raw.IndexOf("PHONEVALUE", StringComparison.Ordinal);
        var result = AliasReplacer.Apply(raw, [Protect(PiiType.Phone, start, "PHONEVALUE".Length, 1)]);

        Assert.Equal("customer_[전화번호1].txt", result);
    }

    // ---- 49/50. 50k / 100k input ----
    [Theory]
    [InlineData(50_000)]
    [InlineData(100_000)]
    public void LargeInput_NoException(int size)
    {
        var raw = new string('A', size) + "PHONE" + new string('B', size);
        var result = AliasReplacer.Apply(raw, [Protect(PiiType.Phone, size, 5, 1)]);

        Assert.Equal(new string('A', size) + "[전화번호1]" + new string('B', size), result);
    }

    // ---- 51. large number of non-overlapping Protect spans ----
    [Fact]
    public void ManyNonOverlappingSpans_NoException()
    {
        const int count = 5000;
        var chars = new char[count * 2];
        for (int i = 0; i < count; i++) { chars[i * 2] = 'X'; chars[i * 2 + 1] = 'Y'; }
        var raw = new string(chars);

        var assignments = new List<AliasAssignment>(count);
        for (int i = 0; i < count; i++)
        {
            assignments.Add(Protect(PiiType.Phone, start: i * 2, length: 2, number: i + 1, canonical: $"V{i}"));
        }

        var result = AliasReplacer.Apply(raw, assignments);
        Assert.DoesNotContain("XY", result);
    }

    // ---- 52. input metadata (assignments / underlying candidates) unchanged after the call ----
    [Fact]
    public void InputAssignmentsAndCandidates_UnchangedAfterCall()
    {
        var assignment = Protect(PiiType.Phone, start: 0, length: 5, number: 1);
        var snapshotDecision = assignment.Decision;
        var snapshotCandidate = assignment.Decision.Candidate;

        AliasReplacer.Apply("AAAAA", [assignment]);

        Assert.Equal(snapshotDecision, assignment.Decision);
        Assert.Equal(snapshotCandidate, assignment.Decision.Candidate);
    }

    // ---- 53. synthetic-only -- every value in this file is a hand-built filler fixture, never
    // real PII. ----
}
