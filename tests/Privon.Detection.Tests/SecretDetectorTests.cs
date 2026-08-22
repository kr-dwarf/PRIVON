using Privon.Core;
using Privon.Detection;
using Privon.Detection.Detectors;

namespace Privon.Detection.Tests;

// All secret values in this file are SYNTHETIC placeholders -- never a real credential.
public class SecretDetectorTests
{
    private static IReadOnlyList<DetectionCandidate> Detect(string rawText)
    {
        var view = NormalizedView.Build(rawText);
        var context = new DetectionContext(view);
        return new SecretDetector().Detect(context);
    }

    // ---- 1. API_KEY= (double-quoted) ----
    [Fact]
    public void DetectsApiKeyAssignment_DoubleQuoted()
    {
        var results = Detect("API_KEY=\"synthetic_value_here\"");
        Assert.Single(results);
        Assert.Equal(RiskLevel.Level3, results[0].RiskLevel);
        Assert.Equal(DetectionConfidence.High, results[0].Confidence);
        Assert.Equal("synthetic_value_here", results[0].Canonical.Value);
    }

    // ---- 2. apiKey: (mixed case key, unquoted) ----
    [Fact]
    public void DetectsApiKeyAssignment_MixedCaseKey_Unquoted()
    {
        var results = Detect("apiKey: synthetic_apikey_value_here");
        Assert.Single(results);
        Assert.Equal(DetectionConfidence.High, results[0].Confidence);
    }

    // ---- 3. api-key assignment ----
    [Fact]
    public void DetectsApiKeyAssignment_HyphenatedKey()
    {
        var results = Detect("api-key = synthetic_value_here");
        Assert.Single(results);
    }

    // ---- 4. token assignment (general key -> Medium) ----
    [Fact]
    public void DetectsTokenAssignment_MediumConfidence()
    {
        var results = Detect("token: synthetic_token_here_1234");
        Assert.Single(results);
        Assert.Equal(RiskLevel.Level3, results[0].RiskLevel);
        Assert.Equal(DetectionConfidence.Medium, results[0].Confidence);
    }

    // ---- 5. access_token ----
    [Fact]
    public void DetectsAccessTokenAssignment()
    {
        var results = Detect("access_token=synthetic_access_token_value_5678");
        Assert.Single(results);
        Assert.Equal(DetectionConfidence.Medium, results[0].Confidence);
    }

    // ---- 6. client_secret (single-quoted) ----
    [Fact]
    public void DetectsClientSecretAssignment_SingleQuoted()
    {
        var results = Detect("client_secret='synthetic_secret_here'");
        Assert.Single(results);
        Assert.Equal(DetectionConfidence.High, results[0].Confidence);
        Assert.Equal("synthetic_secret_here", results[0].Canonical.Value);
    }

    // ---- 7. password assignment ----
    [Fact]
    public void DetectsPasswordAssignment()
    {
        var results = Detect("password: SyntheticP@ssw0rd123");
        Assert.Single(results);
        Assert.Equal(DetectionConfidence.High, results[0].Confidence);
    }

    // ---- 8/9/10 quoting styles (covered structurally by tests above; explicit here too) ----
    [Theory]
    [InlineData("secret=\"synthetic_double_quoted_value\"", "synthetic_double_quoted_value")]
    [InlineData("secret='synthetic_single_quoted_value'", "synthetic_single_quoted_value")]
    [InlineData("secret=synthetic_unquoted_value_here", "synthetic_unquoted_value_here")]
    public void DetectsAllQuotingStyles(string rawText, string expectedValue)
    {
        var results = Detect(rawText);
        Assert.Single(results);
        Assert.Equal(expectedValue, results[0].Canonical.Value);
    }

    // ---- 11. Authorization: Bearer ----
    [Fact]
    public void DetectsAuthorizationBearer()
    {
        var results = Detect("Authorization: Bearer synthetic_bearer_token_value_1234");
        Assert.Single(results);
        Assert.Equal(RiskLevel.Level3, results[0].RiskLevel);
        Assert.Equal(DetectionConfidence.High, results[0].Confidence);
    }

    // ---- 12. lowercase bearer, bare (no "Authorization:" prefix) ----
    [Fact]
    public void DetectsLowercaseBareBearer()
    {
        var results = Detect("bearer synthetic_bearer_token_value_5678");
        Assert.Single(results);
        Assert.Equal(DetectionConfidence.High, results[0].Confidence);
    }

    // ---- 13. Bearer token value RawSpan (excludes "Authorization: Bearer " prefix) ----
    [Fact]
    public void BearerToken_RawSpan_ExcludesPrefix()
    {
        const string token = "synthetic_bearer_token_value_1234";
        var rawText = $"Authorization: Bearer {token}";
        var results = Detect(rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(token, rawText.Substring(span.Start, span.Length));
    }

    // ---- 14. assignment value-only RawSpan (excludes key, operator, quotes) ----
    [Fact]
    public void AssignmentValue_RawSpan_ExcludesKeyAndQuotes()
    {
        const string value = "synthetic_value_here";
        var rawText = $"API_KEY=\"{value}\"";
        var results = Detect(rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(value, rawText.Substring(span.Start, span.Length));
    }

    // ---- 15. punctuation boundary ----
    [Fact]
    public void RawSpan_ExcludesTrailingComma()
    {
        const string value = "synthetic_token_value_1234";
        var rawText = $"token={value}, 다음 텍스트";
        var results = Detect(rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(value, rawText.Substring(span.Start, span.Length));
    }

    // ---- 16. zero-width character + RawIndexMap ----
    [Fact]
    public void ZeroWidthCharacterInsideValue_AreDetected_WithCorrectRawSpan()
    {
        var zeroWidthSpace = ((char)0x200B).ToString();
        var value = "synthetic_value" + zeroWidthSpace + "_here_1234";
        var rawText = $"api_key={value}";
        var results = Detect(rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(value, rawText.Substring(span.Start, span.Length));
    }

    // ---- 17. case-sensitive canonical value preserved ----
    [Fact]
    public void CanonicalValue_PreservesOriginalCase()
    {
        var results = Detect("api_key=SynthEtic_MixedCase_Value123");
        Assert.Single(results);
        Assert.Equal("SynthEtic_MixedCase_Value123", results[0].Canonical.Value);
    }

    // ---- 18. different-case secrets are NOT merged into the same canonical value ----
    [Fact]
    public void DifferentCaseSecrets_HaveDifferentCanonicalValues()
    {
        var upper = Detect("api_key=Synthetic_Value_ABC123")[0].Canonical;
        var lower = Detect("api_key=synthetic_value_abc123")[0].Canonical;

        Assert.NotEqual(upper, lower);
    }

    // ---- 19. too-short / trivial placeholder values never detected ----
    [Theory]
    [InlineData("token=true")]
    [InlineData("password=no")]
    [InlineData("api_key=test")]
    [InlineData("secret=none")]
    [InlineData("token=123")]
    public void DoesNotDetect_TrivialPlaceholderValues(string rawText)
    {
        Assert.Empty(Detect(rawText));
    }

    // ---- 20. unrecognized key names (count/version/boolean/etc.) never detected ----
    [Theory]
    [InlineData("count=123")]
    [InlineData("enabled=true")]
    [InlineData("version=1.2.3")]
    [InlineData("name=test")]
    [InlineData("color=blue")]
    public void DoesNotDetect_UnrecognizedKeyAssignments(string rawText)
    {
        Assert.Empty(Detect(rawText));
    }

    // ---- 21. random long identifier alone, no key context ----
    [Fact]
    public void DoesNotDetect_RandomIdentifierAlone()
    {
        var results = Detect("참조: 8f14e45fceea167a5a36dedd4bea2543 확인");
        Assert.Empty(results);
    }

    // ---- 22. UUID alone ----
    [Fact]
    public void DoesNotDetect_UuidAlone()
    {
        var results = Detect("요청 ID: 550e8400-e29b-41d4-a716-446655440000");
        Assert.Empty(results);
    }

    // ---- 23. hash-like string alone ----
    [Fact]
    public void DoesNotDetect_HashLikeStringAlone()
    {
        var results = Detect("커밋 해시: a94a8fe5ccb19ba61c4c0873d391e987982fbbd3");
        Assert.Empty(results);
    }

    // ---- 24. URL query value, too short to clear the general-key threshold ----
    [Fact]
    public void DoesNotDetect_ShortUrlQueryToken()
    {
        var results = Detect("https://example.com/page?token=abc123xyz");
        Assert.Empty(results);
    }

    // ---- 25. other PII detector values are not flagged as secrets ----
    [Fact]
    public void DoesNotDetect_OtherPiiValues()
    {
        var results = Detect(
            "연락처: 010-1234-5678, 이메일: user@example.com, 주민번호: 900101-1234568, " +
            "카드번호: 1234-5678-9012-3456, 계좌번호: 123-456-789012");
        Assert.Empty(results);
    }

    // ---- 26. partially masked value + explicit key context -> detected ----
    [Fact]
    public void DetectsPartiallyMaskedValue_WithKeyContext()
    {
        var results = Detect("API_KEY=abcd********wxyz");
        Assert.Single(results);
        Assert.Equal(RiskLevel.Level3, results[0].RiskLevel);
        Assert.Equal("abcd********wxyz", results[0].Canonical.Value);
    }

    // ---- 27. fully masked value -> deliberately NOT detected (no visible-character evidence
    // at all; protecting a value with zero identifying content has no real benefit). This
    // test locks that decision in. ----
    [Fact]
    public void DoesNotDetect_FullyMaskedValue()
    {
        Assert.Empty(Detect("password=********"));
        Assert.Empty(Detect("secret=********"));
    }

    // ---- 28. already-protected alias token not re-detected (quoted form -- the unquoted
    // form is naturally excluded already, since '[' and Korean characters are outside the
    // unquoted value character class). ----
    [Fact]
    public void DoesNotReDetect_AlreadyAliasedToken()
    {
        var results = Detect("api_key=\"[시크릿1]\"");
        Assert.Empty(results);
    }

    // ---- bonus: key-boundary hardening -- a recognized key name that is actually a suffix
    // of a longer identifier must not match. ----
    [Fact]
    public void DoesNotDetect_KeyNameAsSuffixOfLongerIdentifier()
    {
        var results = Detect("mytoken=synthetic_long_value_1234567890");
        Assert.Empty(results);
    }

    // ==== Phase 2F.1 -- Fail-Closed Hardening ====

    // ---- 1a. unquoted value ending in '.' -> last character is NOT discarded ----
    [Fact]
    public void UnquotedValue_TrailingDot_IsNotTrimmed()
    {
        var results = Detect("token=AbcDef123456789.");
        Assert.Single(results);
        Assert.Equal("AbcDef123456789.", results[0].Canonical.Value);
    }

    // ---- 1b. quoted value: quotes excluded, internal trailing '.' preserved exactly ----
    [Fact]
    public void QuotedValue_TrailingDot_PreservedExactly()
    {
        const string value = "AbcDef123456789.";
        var rawText = $"token=\"{value}\"";
        var results = Detect(rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(value, rawText.Substring(span.Start, span.Length));
        Assert.Equal(value, results[0].Canonical.Value);
    }

    // ---- 1c. JWT-like value: internal dots all preserved ----
    [Fact]
    public void JwtLikeValue_InternalDotsPreserved()
    {
        var results = Detect("token=aaa.bbb.ccc");
        Assert.Single(results);
        Assert.Equal("aaa.bbb.ccc", results[0].Canonical.Value);
    }

    // ---- 1d. trailing semicolon boundary still excludes the delimiter, not value characters ----
    [Fact]
    public void RawSpan_ExcludesTrailingSemicolon_KeepsFullValue()
    {
        const string value = "AbcDef123456789";
        var rawText = $"token={value}; 다음";
        var results = Detect(rawText);

        Assert.Single(results);
        var span = results[0].Span;
        Assert.Equal(value, rawText.Substring(span.Start, span.Length));
    }

    // ---- 2. numeric-only value under a STRONG key, long enough -> detected ----
    [Theory]
    [InlineData("password=1234567890123456")]
    [InlineData("client_secret=98765432109876543210")]
    public void DetectsNumericOnlyValue_UnderStrongKey(string rawText)
    {
        var results = Detect(rawText);
        Assert.Single(results);
        Assert.Equal(RiskLevel.Level3, results[0].RiskLevel);
        Assert.Equal(DetectionConfidence.High, results[0].Confidence);
    }

    // ---- 2. same-shaped numeric value under an unrecognized key is still never a candidate ----
    [Theory]
    [InlineData("count=1234567890123456")]
    [InlineData("order=1234567890123456")]
    public void DoesNotDetect_NumericValue_UnderUnrecognizedKey(string rawText)
    {
        Assert.Empty(Detect(rawText));
    }

    // ---- 2. numeric-only leniency is scoped to STRONG keys only -- general keys still
    // require character-class diversity ----
    [Fact]
    public void DoesNotDetect_NumericOnlyValue_UnderGeneralKey()
    {
        Assert.Empty(Detect("token=1234567890123456"));
    }

    // ---- 2. numeric-only leniency has its own (higher) minimum length ----
    [Fact]
    public void DoesNotDetect_ShortNumericOnlyValue_UnderStrongKey()
    {
        // 9 digits clears StrongKeyMinLength (6) but not the numeric-only threshold (12).
        Assert.Empty(Detect("password=123456789"));
    }

    // ---- 3. CanonicalValue reflects the RAW characters at the span -- a zero-width
    // character difference between two raw values must not be erased into the same
    // canonical value by normalization. ----
    [Fact]
    public void CanonicalValue_DoesNotMergeDifferentRawValues_DueToZeroWidthNormalization()
    {
        var zeroWidthSpace = ((char)0x200B).ToString();
        var withoutZeroWidth = Detect("API_KEY=\"Abcd1234\"")[0].Canonical;
        var withZeroWidth = Detect($"API_KEY=\"Abcd{zeroWidthSpace}1234\"")[0].Canonical;

        Assert.NotEqual(withoutZeroWidth, withZeroWidth);
        Assert.Equal("Abcd1234", withoutZeroWidth.Value);
        Assert.Equal("Abcd" + zeroWidthSpace + "1234", withZeroWidth.Value);
    }

    // ---- 29. mixed detector sentence: Secret detects only its own value ----
    [Fact]
    public void CoexistsWithOtherDetectors_DetectsOnlyItsOwnValue()
    {
        const string secretValue = "synthetic_secret_value_9999";
        var rawText =
            $"연락처: 010-1234-5678, 이메일: user@example.com, 주민번호: 900101-1234568, " +
            $"카드번호: 1234-5678-9012-3456, 계좌번호: 123-456-789012, api_key={secretValue}";

        var view = NormalizedView.Build(rawText);
        var context = new DetectionContext(view);

        var phoneResults = new PhoneDetector().Detect(context);
        var emailResults = new EmailDetector().Detect(context);
        var rrnResults = new ResidentRegistrationNumberDetector().Detect(context);
        var cardResults = new CardNumberDetector().Detect(context);
        var accountResults = new BankAccountNumberDetector().Detect(context);
        var secretResults = new SecretDetector().Detect(context);

        Assert.Single(phoneResults);
        Assert.Single(emailResults);
        Assert.Single(rrnResults);
        Assert.Single(cardResults);
        Assert.Single(accountResults);
        Assert.Single(secretResults);
        Assert.Equal(secretValue, secretResults[0].Canonical.Value);

        var allSpans = new[]
        {
            phoneResults[0].Span, emailResults[0].Span, rrnResults[0].Span,
            cardResults[0].Span, accountResults[0].Span, secretResults[0].Span,
        };
        for (int i = 0; i < allSpans.Length; i++)
        {
            for (int j = i + 1; j < allSpans.Length; j++)
            {
                Assert.False(allSpans[i].OverlapsWith(allSpans[j]));
            }
        }
    }

    // ---- 30. very long input never throws ----
    [Fact]
    public void VeryLongInput_NoException()
    {
        var longText = string.Concat(Enumerable.Repeat("일반 텍스트 문장입니다. ", 20000)) + "api_key=synthetic_value_here";
        var results = Detect(longText);
        Assert.Single(results);
    }

    // ---- empty input never throws ----
    [Fact]
    public void EmptyString_NoException()
    {
        var results = Detect("");
        Assert.Empty(results);
    }
}
