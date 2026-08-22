using Privon.Core;
using Privon.Detection;
using Privon.Detection.Detectors;

namespace Privon.Detection.Tests;

// Phase 2B.1 -- Email Boundary Hardening. Confirms EmailDetector never carves a "clean"
// email-shaped substring out of a larger, invalid, glued-together token, while still
// detecting genuinely well-formed addresses embedded in ordinary prose/punctuation.
public class EmailBoundaryTests
{
    private static IReadOnlyList<DetectionCandidate> Detect(string rawText)
    {
        var view = NormalizedView.Build(rawText);
        var context = new DetectionContext(view);
        return new EmailDetector().Detect(context);
    }

    // ---- 1. double dot before the recoverable suffix ----
    [Fact]
    public void DoesNotDetect_SuffixInsideDoubleDotToken()
    {
        var results = Detect("주소: user..name@example.com 확인 필요.");
        Assert.Empty(results);
    }

    // ---- 2. triple dot before the recoverable suffix ----
    [Fact]
    public void DoesNotDetect_SuffixInsideTripleDotToken()
    {
        var results = Detect("주소: user...name@example.com 확인 필요.");
        Assert.Empty(results);
    }

    // ---- 3. leading dot ----
    [Fact]
    public void DoesNotDetect_SuffixAfterLeadingDot()
    {
        var results = Detect("주소: .user@example.com 확인 필요.");
        Assert.Empty(results);
    }

    // ---- 4. trailing dot directly before '@' ----
    [Fact]
    public void DoesNotDetect_LocalPartWithTrailingDotBeforeAt()
    {
        var results = Detect("주소: user.@example.com 확인 필요.");
        Assert.Empty(results);
    }

    // ---- 5. glued prefix/suffix letters: whole contiguous token, never a truncated inner piece ----
    [Fact]
    public void GluedLetterToken_DetectsWholeTokenOnly_NeverTruncatedFragment()
    {
        var rawText = "참고: abcUSER@example.comxyz 였습니다.";
        var results = Detect(rawText);

        // No character class boundary is actually broken here (letters are valid on both
        // sides and Tld already greedily swallows "comxyz"), so this is not a "..".-style
        // fragment case -- but the boundary check must not mis-fire and chop off "abc"/"xyz"
        // either. Whatever is reported (if anything) must span the entire glued run, never a
        // truncated inner substring such as "USER@example.com".
        if (results.Count > 0)
        {
            Assert.Single(results);
            var span = results[0].Span;
            Assert.Equal("abcUSER@example.comxyz", rawText.Substring(span.Start, span.Length));
        }
    }

    // ---- 6. plain sentence, normal detection unaffected ----
    [Fact]
    public void DetectsNormally_InPlainSentence()
    {
        var results = Detect("문의: user@example.com 입니다");
        Assert.Single(results);
        Assert.Equal("user@example.com", results[0].Canonical.Value);
    }

    // ---- 7. parenthesized, exact span only ----
    [Fact]
    public void DetectsNormally_Parenthesized()
    {
        const string email = "user@example.com";
        var rawText = $"({email})";
        var results = Detect(rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(email, rawText.Substring(span.Start, span.Length));
    }

    // ---- 8. trailing comma / trailing period, excluded from span ----
    [Theory]
    [InlineData("user@example.com, 다음", "user@example.com")]
    [InlineData("user@example.com.", "user@example.com")]
    public void DetectsNormally_TrailingPunctuationExcluded(string rawText, string expectedEmail)
    {
        var results = Detect(rawText);
        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(expectedEmail, rawText.Substring(span.Start, span.Length));
    }

    // ---- 9. obfuscated-email regression ----
    [Theory]
    [InlineData("user(at)example.com")]
    [InlineData("user [at] example.com")]
    [InlineData("user@example(dot)com")]
    [InlineData("user (at) example (dot) com")]
    public void ObfuscatedEmails_StillDetected(string email)
    {
        var results = Detect($"연락처: {email} 입니다.");
        Assert.Single(results);
        Assert.Equal(DetectionConfidence.Medium, results[0].Confidence);
    }
}
