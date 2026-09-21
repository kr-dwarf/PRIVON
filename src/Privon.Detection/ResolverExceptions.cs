using System.Globalization;

namespace Privon.Detection;

/// <summary>
/// D3B0-A A.9 §15 -- R0. EXPLICIT_ABSENCE: an ABSENT field is NOT EMITTED AT ALL; not null, not
/// 0, not "", not a default enum member, not a sentinel.
/// </summary>
public sealed class InvalidCandidateSpanException : Exception
{
    public const string Code = "RESOLVER_INVALID_CANDIDATE_SPAN";

    private readonly string _message;

    internal InvalidCandidateSpanException(int invalidSpanStart, int invalidSpanLength, int rawInputLength)
        : base(Build(invalidSpanStart, invalidSpanLength, rawInputLength))
    {
        InvalidSpanStart = invalidSpanStart;
        InvalidSpanLength = invalidSpanLength;
        RawInputLength = rawInputLength;
        _message = Build(invalidSpanStart, invalidSpanLength, rawInputLength);
    }

    public FailureKind FailureKind => FailureKind.ResolverSpanValidationFailure;
    public string ErrorCode => Code;

    /// <summary>A CANDIDATE'S OWN span start -- deliberately named differently from an overlap region's SpanStart.</summary>
    public int InvalidSpanStart { get; }
    public int InvalidSpanLength { get; }
    public int RawInputLength { get; }

    public override string Message => _message;

    private static string Build(int start, int length, int rawInputLength) =>
        Code + ": start=" + start.ToString(CultureInfo.InvariantCulture)
             + "; length=" + length.ToString(CultureInfo.InvariantCulture)
             + "; rawInputLength=" + rawInputLength.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// D3B0-A A.9 §15 -- R0.5 check 1. ONE_TYPE_PER_FAILURE_CLASS: never collapsed into
/// <see cref="InvalidNormativeValueDomainException"/>.
///
/// The payload is the accepted CONSTANT: no field name, no candidate identity, no index, no
/// value. It is a SCAN DIAGNOSTIC, which is why it stays constant while
/// NormativeFieldBagConstructionException may name its offending key (§7.3).
/// </summary>
public sealed class UnknownCandidateSemanticFieldException : Exception
{
    public const string Code = "RESOLVER_UNKNOWN_NORMATIVE_SEMANTIC_FIELD";

    internal UnknownCandidateSemanticFieldException() : base(Code) { }

    public FailureKind FailureKind => FailureKind.ResolverDomainValidationFailure;
    public string ErrorCode => Code;
}

/// <summary>
/// D3B0-A A.9 §15 -- R0.5 checks 2 and 3. One exception TYPE carrying two distinct ErrorCodes,
/// exactly as the accepted union specifies; the message renders the ErrorCode literal verbatim.
/// </summary>
public sealed class InvalidNormativeValueDomainException : Exception
{
    public const string UnknownEnumValueCode = "RESOLVER_UNKNOWN_NORMATIVE_ENUM_VALUE";
    public const string InvalidUnicodeCode = "RESOLVER_INVALID_UNICODE_SCALAR_SEQUENCE";

    internal InvalidNormativeValueDomainException(string errorCode) : base(errorCode)
        => ErrorCode = errorCode;

    public FailureKind FailureKind => FailureKind.ResolverDomainValidationFailure;
    public string ErrorCode { get; }
}

/// <summary>
/// D3B0-A A.9 §15 -- R3. SpanStart/SpanLength here describe the OVERLAP REGION, deliberately
/// named differently from InvalidSpanStart/InvalidSpanLength (a candidate's own span).
/// </summary>
public sealed class UnresolvableCandidateTieException : Exception
{
    public const string Code = "RESOLVER_AMBIGUOUS_EQUAL_KEY_OVERLAP";

    private readonly string _message;

    internal UnresolvableCandidateTieException(
        ArbitrationDomain domain, int spanStart, int spanLength, DetectorId low, DetectorId high)
        : base(Build(domain, spanStart, spanLength, low, high))
    {
        ArbitrationDomain = domain;
        SpanStart = spanStart;
        SpanLength = spanLength;
        LowDetectorId = low;
        HighDetectorId = high;
        _message = Build(domain, spanStart, spanLength, low, high);
    }

    public FailureKind FailureKind => FailureKind.ResolverAmbiguityFailure;
    public string ErrorCode => Code;
    public ArbitrationDomain ArbitrationDomain { get; }
    public int SpanStart { get; }
    public int SpanLength { get; }
    public DetectorId LowDetectorId { get; }
    public DetectorId HighDetectorId { get; }

    public override string Message => _message;

    private static string Build(ArbitrationDomain domain, int start, int length, DetectorId low, DetectorId high) =>
        Code + ": domain=" + domain.ToString()
             + "; span=" + start.ToString(CultureInfo.InvariantCulture)
             + ":" + length.ToString(CultureInfo.InvariantCulture)
             + "; low=" + low.ToString()
             + "; high=" + high.ToString();
}
