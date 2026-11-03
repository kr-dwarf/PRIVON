using Privon.Detection;

namespace Privon.UsSsn;

// EXPERIMENT ONLY -- NOT AN OFFICIAL DETECTOR, NOT REGISTERED, NOT A SECURITY DETERMINATION.
// An isolated, non-operative implementation of the frozen D2.2 / D3A U.S. SSN structure and context rules,
// used to exercise those rules against synthetic data. It is not wired to DetectorRegistry, DetectionPipeline,
// any Readiness state, or any product path. Types here carry no raw personal data in their diagnostic output.

/// <summary>Frozen oracle evidence letters: S = structure, C = context (a strong label).</summary>
[Flags]
public enum UsSsnEvidence
{
    None = 0,
    Structure = 1,
    Context = 2,
}

public enum UsSsnForm
{
    Compact,
    Dashed,
    Spaced,
}

/// <summary>
/// Why a number-shaped input produced no candidate. The first eight members mirror the frozen D3A oracle reason
/// codes; the rest are experiment (EA) reasons for branches the frozen corpus does not decide.
/// </summary>
public enum UsSsnRejectionReason
{
    Area000,
    Area666,
    Area900To999,
    Group00,
    Serial0000,
    UnsupportedSeparator,
    NonAsciiDigit,
    AlphanumericBoundaryViolation,
    MixedSeparators,
    TrailingContent,
    UnrecognizedValueShape,
    FieldBindingRejected,
}

/// <summary>
/// The nine canonical ASCII digits of a candidate. Sensitive: <see cref="ToString"/> prints nothing derived from
/// the value (no length, prefix or hash), matching the precedent of <c>Privon.Detection.CanonicalValue</c>.
/// </summary>
public readonly struct UsSsnCanonical
{
    private readonly string? _digits;

    internal UsSsnCanonical(string digits) => _digits = digits;

    /// <summary>Returns the sensitive digits. Named for its only intended use: synthetic tests.</summary>
    public string GetDigitsForTestingOnly() => _digits ?? string.Empty;

    /// <summary>Product-candidate construction only (internal; visible to the isolated integration assembly).</summary>
    internal string GetValueForProductCandidate() => _digits ?? string.Empty;

    public override string ToString() => $"{nameof(UsSsnCanonical)} {{ Redacted }}";
}

public sealed record UsSsnCandidate(RawSpan Span, UsSsnEvidence Evidence, UsSsnForm Form, UsSsnCanonical Canonical)
{
    public override string ToString() => $"{nameof(UsSsnCandidate)} {{ Evidence = {Evidence}, Form = {Form} }}";
}

public sealed record UsSsnRejection(RawSpan Span, UsSsnRejectionReason Reason)
{
    public override string ToString() => $"{nameof(UsSsnRejection)} {{ Reason = {Reason} }}";
}

public sealed class UsSsnScanResult
{
    public const string Notice = "CANDIDATES_ONLY_NOT_A_SECURITY_DETERMINATION";

    public static readonly UsSsnScanResult Empty = new([], []);

    internal UsSsnScanResult(IReadOnlyList<UsSsnCandidate> candidates, IReadOnlyList<UsSsnRejection> rejections)
    {
        Candidates = new List<UsSsnCandidate>(candidates).AsReadOnly();
        Rejections = new List<UsSsnRejection>(rejections).AsReadOnly();
    }

    public IReadOnlyList<UsSsnCandidate> Candidates { get; }

    public IReadOnlyList<UsSsnRejection> Rejections { get; }

    public override string ToString() =>
        $"{nameof(UsSsnScanResult)} {{ Candidates = {Candidates.Count}, Rejections = {Rejections.Count} }}";
}
