using Privon.Detection;

namespace Privon.UsMbiSevis;

// EXPERIMENT ONLY -- NOT AN OFFICIAL DETECTOR, NOT REGISTERED, NOT A SECURITY DETERMINATION.
// An isolated, non-operative implementation of the frozen D2.2 / D3A U.S. Medicare MBI and SEVIS ID structure rules,
// used to exercise those rules against synthetic data. It is not wired to DetectorRegistry, DetectionPipeline, any
// Readiness state, or any product path. Types here carry no raw personal data in their diagnostic output.

/// <summary>Frozen oracle evidence letters: S = structure, C = context (a strong label).</summary>
[Flags]
public enum UsIdEvidence
{
    None = 0,
    Structure = 1,
    Context = 2,
}

/// <summary>
/// The canonical ASCII form of a candidate (MBI: 11 upper-case characters; SEVIS: N + 10 digits). Sensitive:
/// <see cref="ToString"/> prints nothing derived from the value (no length, prefix or hash), matching the precedent
/// of <c>Privon.Detection.CanonicalValue</c>.
/// </summary>
public readonly struct UsIdCanonical
{
    private readonly string? _value;

    internal UsIdCanonical(string value) => _value = value;

    /// <summary>Returns the sensitive value. Named for its only intended use: synthetic tests.</summary>
    public string GetValueForTestingOnly() => _value ?? string.Empty;

    /// <summary>Product-candidate construction only (internal; visible to the isolated integration assembly).</summary>
    internal string GetValueForProductCandidate() => _value ?? string.Empty;

    public override string ToString() => $"{nameof(UsIdCanonical)} {{ Redacted }}";
}

// ---- MBI ------------------------------------------------------------------------------------------------------------

public enum UsMbiForm
{
    Compact,
    Hyphenated,
    Spaced,
}

/// <summary>
/// Why an MBI-shaped input produced no candidate. The Excluded* members, the position members, WrongLength,
/// BoundaryViolation, NonAsciiLookalike, BadGrouping and MixedSeparators mirror the frozen D3A oracle reason codes
/// (see the tests); the rest are experiment (EA) reasons for branches the frozen corpus does not decide.
/// </summary>
public enum UsMbiRejectionReason
{
    Position1RequiresOneToNine,
    PositionRequiresAllowedLetter,
    PositionRequiresDigit,
    ExcludedB,
    ExcludedI,
    ExcludedL,
    ExcludedO,
    ExcludedS,
    ExcludedZ,
    WrongLength,
    BoundaryViolation,
    NonAsciiLookalike,
    BadGrouping,
    MixedSeparators,
    UnsupportedSeparator,
    TrailingContent,
    UnrecognizedValueShape,
    FieldBindingRejected,
}

public sealed record UsMbiCandidate(RawSpan Span, UsIdEvidence Evidence, UsMbiForm Form, UsIdCanonical Canonical)
{
    public override string ToString() => $"{nameof(UsMbiCandidate)} {{ Evidence = {Evidence}, Form = {Form} }}";
}

/// <param name="Position">1-based MBI position that failed, for the position-rule reasons only; otherwise null.</param>
public sealed record UsMbiRejection(RawSpan Span, UsMbiRejectionReason Reason, int? Position = null)
{
    public override string ToString() =>
        $"{nameof(UsMbiRejection)} {{ Reason = {Reason}{(Position is { } p ? $", Position = {p}" : string.Empty)} }}";
}

public sealed class UsMbiResult
{
    public const string Notice = "CANDIDATES_ONLY_NOT_A_SECURITY_DETERMINATION";

    public static readonly UsMbiResult Empty = new([], []);

    internal UsMbiResult(IReadOnlyList<UsMbiCandidate> candidates, IReadOnlyList<UsMbiRejection> rejections)
    {
        Candidates = new List<UsMbiCandidate>(candidates).AsReadOnly();
        Rejections = new List<UsMbiRejection>(rejections).AsReadOnly();
    }

    public IReadOnlyList<UsMbiCandidate> Candidates { get; }

    public IReadOnlyList<UsMbiRejection> Rejections { get; }

    public override string ToString() =>
        $"{nameof(UsMbiResult)} {{ Candidates = {Candidates.Count}, Rejections = {Rejections.Count} }}";
}

// ---- SEVIS ----------------------------------------------------------------------------------------------------------

/// <summary>
/// Why a SEVIS-shaped input produced no candidate. WrongLength, PrefixMustBeN, NonAsciiDigit and BoundaryViolation
/// mirror the frozen D3A oracle reason codes; the rest are experiment (EA) reasons.
/// </summary>
public enum UsSevisRejectionReason
{
    WrongLength,
    PrefixMustBeN,
    NonAsciiDigit,
    BoundaryViolation,
    NonAsciiLookalike,
    TrailingContent,
    UnrecognizedValueShape,
    FieldBindingRejected,
}

public sealed record UsSevisCandidate(RawSpan Span, UsIdEvidence Evidence, UsIdCanonical Canonical)
{
    public override string ToString() => $"{nameof(UsSevisCandidate)} {{ Evidence = {Evidence} }}";
}

public sealed record UsSevisRejection(RawSpan Span, UsSevisRejectionReason Reason)
{
    public override string ToString() => $"{nameof(UsSevisRejection)} {{ Reason = {Reason} }}";
}

public sealed class UsSevisResult
{
    public const string Notice = UsMbiResult.Notice;

    public static readonly UsSevisResult Empty = new([], []);

    internal UsSevisResult(IReadOnlyList<UsSevisCandidate> candidates, IReadOnlyList<UsSevisRejection> rejections)
    {
        Candidates = new List<UsSevisCandidate>(candidates).AsReadOnly();
        Rejections = new List<UsSevisRejection>(rejections).AsReadOnly();
    }

    public IReadOnlyList<UsSevisCandidate> Candidates { get; }

    public IReadOnlyList<UsSevisRejection> Rejections { get; }

    public override string ToString() =>
        $"{nameof(UsSevisResult)} {{ Candidates = {Candidates.Count}, Rejections = {Rejections.Count} }}";
}
