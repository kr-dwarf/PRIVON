using Privon.Detection;

namespace Privon.UsSsn;

// EXPERIMENT ONLY -- NOT AN OFFICIAL DETECTOR, NOT REGISTERED, NOT A SECURITY DETERMINATION. See UsSsnTypes.cs.
//
// U.S. taxpayer family (ATIN / ITIN / SSN) as pure functions with two explicit modes:
//
//   TargetDetectorOnly  - one type is evaluated and reported on its own (the D3-NEG contract: a negative asserts ZERO
//                         candidates from the TARGET detector). A value bound to a field owned by any other
//                         recognized label is not reinterpreted as the target's unlabeled number.
//   TaxFamilyResolver   - all three types judge each number; accepted ones are arbitrated ATIN > ITIN > SSN so one value
//                         is never emitted as more than one type. A value under a taxpayer label is shared by the
//                         family; a value inside a NON-tax recognized field belongs to that field and is not claimed.
//
// Evaluate(...) is the earlier entry point and is kept as an alias of TaxFamilyResolver.
//
// Contract basis (Notion "040-US-D2.2", read-only) and the frozen D3A corpus: ITIN = 9xx area with group 50-65, 70-88,
// 90-92 or 94-99; ATIN = 9xx area with group 93; SSN = the existing SSN rules (reused from UsSsnScanner).
// A logical field is one recognized label plus its single bound value: 0-3 spaces, optional one of ':' '#' '=', 0-3
// spaces, value starts immediately, binding ends at the earliest of CR LF ';' HT '|' or the start of another recognized
// label, at most 96 raw characters. That binding is delegated to the shared FieldBinder.
//
// Generic field ownership: every recognized label's bound value is a region owned by that label, and a number inside a
// region is not reinterpreted as another type's unlabeled number (see the two modes above). The shared FieldBinder now
// ends a field at the START of another recognized label, including the first value position (D2.2), so a field that
// begins with another label arrives as an empty field and that label arrives as its own binding. This module therefore
// makes ONE pass over the binder output: no re-binding, no special-cased label or text, and no limit that could end
// label collection early (an unfinished scan is never reported as "no candidates").
//
// KNOWN LIMITS (explicit, not guessed):
//   * The recognized-label set is an input. Only labels enumerated in D2.2 text that was read are built in (taxpayer
//     labels; I-94, address and name labels). Labels of other detectors (phone, bank, A-Number, passport, driver
//     licence, MBI, SEVIS, ...) must be supplied via WithOthers; without them their fields are not recognized.
//   * ITIN and ATIN have no serial-number rule and accept any 9xx area; the corpus shows none.
//   * The binder matches a label anywhere (grammar-free). This module skips a label inside a word [EA]; the binder
//     itself still ends the PRECEDING field at such a label, which D2.2 does not specify either way.
//   * Labelled-value defects beyond the frozen reasons (trailing content, mixed separators) are experiment reasons.

/// <summary>Enum values are the frozen oracle ranks and are used ONLY to order arbitration here (lower wins). They are not registry ranks.</summary>
public enum UsTaxpayerKind
{
    Atin = 50,
    Itin = 60,
    Ssn = 70,
}

public enum UsTaxpayerRejectionReason
{
    // SSN (frozen oracle reasons)
    Area000,
    Area666,
    Area900To999,
    Group00,
    Serial0000,
    // ITIN
    ItinAreaNotNine,
    ItinRangeBelow50,
    ItinRangeGap66To69,
    ItinRange89Excluded,
    ItinRange93Excluded,
    // ATIN
    AtinAreaNotNine,
    AtinRequires93,
    // shared (frozen oracle reasons first, then [EA] reasons)
    UnsupportedSeparator,
    NonAsciiDigit,
    AlphanumericBoundaryViolation,
    InvalidLength,
    NextRecognizedFieldTerminatesBinding,
    MixedSeparators,
    TrailingContent,
    UnrecognizedValueShape,
    FieldBindingRejected,
}

public enum UsTaxpayerAttemptOutcome
{
    Accepted,
    Rejected,
    Superseded,
}

public sealed record UsTaxpayerCandidate(
    UsTaxpayerKind Kind, RawSpan Span, UsSsnEvidence Evidence, UsSsnForm Form, UsSsnCanonical Canonical)
{
    public override string ToString() =>
        $"{nameof(UsTaxpayerCandidate)} {{ Kind = {Kind}, Evidence = {Evidence}, Form = {Form} }}";
}

/// <summary>One type's verdict on one number: accepted, rejected (with a reason) or superseded by a higher-priority type.</summary>
public sealed record UsTaxpayerAttempt(
    UsTaxpayerKind Kind, RawSpan Span, UsSsnEvidence Evidence, UsTaxpayerAttemptOutcome Outcome, UsTaxpayerRejectionReason? Reason)
{
    public override string ToString() =>
        $"{nameof(UsTaxpayerAttempt)} {{ Kind = {Kind}, Outcome = {Outcome}, Reason = {Reason?.ToString() ?? "None"} }}";
}

public sealed record UsTaxpayerLabelSet(
    IReadOnlyList<string> Ssn, IReadOnlyList<string> Itin, IReadOnlyList<string> Atin, IReadOnlyList<string> Others)
{
    // Non-tax recognized labels enumerated in D2.2 (I-94, address, name). Other detectors' labels are NOT enumerated here.
    private static readonly string[] ConfirmedNonTaxLabels =
    [
        "I-94", "I94", "I-94 Number", "I94 Number", "I-94 Admission Number", "Form I-94 Number",
        "Address", "Home Address", "Mailing Address", "Residential Address", "Street Address",
        "Full Name", "First Name", "Given Name", "Last Name", "Family Name", "Surname", "Name",
    ];

    public static UsTaxpayerLabelSet Default { get; } = new(
        UsSsnScanner.DefaultStrongLabels,
        Array.AsReadOnly(["ITIN"]),
        Array.AsReadOnly(["ATIN", "Adoption Taxpayer Identification Number"]),
        Array.AsReadOnly(ConfirmedNonTaxLabels));

    /// <summary>Adds labels of other fields; they only end a tax field's value and own their own value region.</summary>
    public UsTaxpayerLabelSet WithOthers(params string[] others) =>
        this with { Others = Array.AsReadOnly(Others.Concat(others).ToArray()) };
}

public sealed class UsTaxpayerFamilyResult
{
    public const string Notice = "CANDIDATES_ONLY_NOT_A_SECURITY_DETERMINATION";

    public static readonly UsTaxpayerFamilyResult Empty = new([], []);

    internal UsTaxpayerFamilyResult(IReadOnlyList<UsTaxpayerCandidate> candidates, IReadOnlyList<UsTaxpayerAttempt> attempts)
    {
        Candidates = new List<UsTaxpayerCandidate>(candidates).AsReadOnly();
        Attempts = new List<UsTaxpayerAttempt>(attempts).AsReadOnly();
    }

    /// <summary>The emitted candidates: in family mode at most one type per number, in position order.</summary>
    public IReadOnlyList<UsTaxpayerCandidate> Candidates { get; }

    /// <summary>Every per-type verdict, including rejected and superseded ones. Nothing here is emitted.</summary>
    public IReadOnlyList<UsTaxpayerAttempt> Attempts { get; }

    public override string ToString() =>
        $"{nameof(UsTaxpayerFamilyResult)} {{ Candidates = {Candidates.Count}, Attempts = {Attempts.Count} }}";
}

/// <summary>ATIN &gt; ITIN &gt; SSN, with overlapping spans mutually exclusive. Pure and independent of input order.</summary>
public static class UsTaxpayerArbiter
{
    public static (IReadOnlyList<UsTaxpayerCandidate> Emitted, IReadOnlyList<UsTaxpayerCandidate> Superseded) Resolve(
        IReadOnlyList<UsTaxpayerCandidate> accepted)
    {
        ArgumentNullException.ThrowIfNull(accepted);

        var ordered = accepted
            .Distinct()
            .OrderBy(c => (int)c.Kind)
            .ThenBy(c => c.Span.Start).ThenBy(c => c.Span.Length)
            .ThenBy(c => (int)c.Evidence).ThenBy(c => (int)c.Form)
            .ThenBy(c => c.Canonical.GetDigitsForTestingOnly(), StringComparer.Ordinal)
            .ToList();

        var emitted = new List<UsTaxpayerCandidate>();
        var superseded = new List<UsTaxpayerCandidate>();
        foreach (var candidate in ordered)
        {
            if (emitted.Any(kept => kept.Span.OverlapsWith(candidate.Span)))
            {
                superseded.Add(candidate);
            }
            else
            {
                emitted.Add(candidate);
            }
        }

        return (
            emitted.OrderBy(c => c.Span.Start).ThenBy(c => c.Span.Length).ToList().AsReadOnly(),
            superseded.AsReadOnly());
    }
}

public static class UsTaxpayerFamily
{
    /// <summary>Alias of <see cref="EvaluateTaxFamilyResolver(string?)"/>, kept for compatibility.</summary>
    public static UsTaxpayerFamilyResult Evaluate(string? rawText) => EvaluateTaxFamilyResolver(rawText);

    public static UsTaxpayerFamilyResult Evaluate(string? rawText, UsTaxpayerLabelSet labels) =>
        EvaluateTaxFamilyResolver(rawText, labels);

    /// <summary>TaxFamilyResolver mode: all three types judge each number and ATIN &gt; ITIN &gt; SSN arbitration applies.</summary>
    public static UsTaxpayerFamilyResult EvaluateTaxFamilyResolver(string? rawText) =>
        EvaluateTaxFamilyResolver(rawText, UsTaxpayerLabelSet.Default);

    public static UsTaxpayerFamilyResult EvaluateTaxFamilyResolver(string? rawText, UsTaxpayerLabelSet labels) =>
        EvaluateCore(rawText, labels, target: null);

    /// <summary>TargetDetectorOnly mode: only <paramref name="target"/> is evaluated and reported.</summary>
    public static UsTaxpayerFamilyResult EvaluateTargetDetectorOnly(string? rawText, UsTaxpayerKind target) =>
        EvaluateTargetDetectorOnly(rawText, target, UsTaxpayerLabelSet.Default);

    public static UsTaxpayerFamilyResult EvaluateTargetDetectorOnly(string? rawText, UsTaxpayerKind target, UsTaxpayerLabelSet labels)
    {
        if (!Enum.IsDefined(target))
        {
            throw new ArgumentOutOfRangeException(nameof(target));
        }

        return EvaluateCore(rawText, labels, target);
    }

    private static UsTaxpayerFamilyResult EvaluateCore(string? rawText, UsTaxpayerLabelSet labels, UsTaxpayerKind? target)
    {
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(labels.Ssn);
        ArgumentNullException.ThrowIfNull(labels.Itin);
        ArgumentNullException.ThrowIfNull(labels.Atin);
        ArgumentNullException.ThrowIfNull(labels.Others);

        if (string.IsNullOrEmpty(rawText))
        {
            return UsTaxpayerFamilyResult.Empty;
        }

        var run = new Run(rawText, labels, target);
        run.CollectLabelled();
        run.CollectUnlabelledDashed();
        return run.Finish();
    }

    /// <summary>Builds a candidate directly, so arbitration can be exercised on synthetic overlaps. Not a detection path.</summary>
    public static UsTaxpayerCandidate CreateCandidateForTesting(
        UsTaxpayerKind kind, RawSpan span, UsSsnEvidence evidence, UsSsnForm form, string digits) =>
        new(kind, span, evidence, form, new UsSsnCanonical(digits));

    // One evaluation pass over one text.
    private sealed class Run
    {
        private sealed record Window(RawSpan Span, UsSsnForm Form, string Digits, UsTaxpayerKind? LabelKind);

        // The bound value of one recognized field; Owner is the taxpayer type of its label, or null for a non-tax label.
        private readonly record struct Region(RawSpan Span, UsTaxpayerKind? Owner);

        private static readonly UsTaxpayerKind[] AllKinds = [UsTaxpayerKind.Atin, UsTaxpayerKind.Itin, UsTaxpayerKind.Ssn];

        private readonly string _raw;
        private readonly UsTaxpayerKind? _target;
        private readonly UsTaxpayerKind[] _kinds;
        private readonly List<FieldLabel> _allLabels = [];
        private readonly Dictionary<string, UsTaxpayerKind> _kindByLabel = new(StringComparer.Ordinal);
        private readonly List<UsTaxpayerAttempt> _rejected = [];
        private readonly Dictionary<RawSpan, Window> _windows = [];
        private readonly List<Region> _regions = [];

        public Run(string raw, UsTaxpayerLabelSet labels, UsTaxpayerKind? target)
        {
            _raw = raw;
            _target = target;
            _kinds = target is { } t ? [t] : AllKinds;

            // Priority order decides a label that is declared for more than one type.
            AddLabels(labels.Atin, UsTaxpayerKind.Atin);
            AddLabels(labels.Itin, UsTaxpayerKind.Itin);
            AddLabels(labels.Ssn, UsTaxpayerKind.Ssn);
            foreach (var text in labels.Others)
            {
                if (FieldLabel.TryCreate(text, out var label) && !_allLabels.Contains(label)) _allLabels.Add(label);
            }
        }

        private void AddLabels(IReadOnlyList<string> texts, UsTaxpayerKind kind)
        {
            foreach (var text in texts)
            {
                if (!FieldLabel.TryCreate(text, out var label)) continue;
                if (!_allLabels.Contains(label)) _allLabels.Add(label);
                _kindByLabel.TryAdd(label.Value, kind);
            }
        }

        // A taxpayer type takes part in this run (family: every type; target mode: only the target).
        private bool Relevant(UsTaxpayerKind kind) => _target is null || _target == kind;

        // ---- labelled fields ------------------------------------------------------------------------------------

        public void CollectLabelled()
        {
            if (_allLabels.Count == 0) return;

            // One pass over the shared binder. Per D2.2 it ends a field at the start of another recognized label (including
            // the first value position), so a field that begins with another label arrives here as an empty field and that
            // label arrives as its own binding: no re-binding, and no limit on how many labels may follow one another.
            foreach (var binding in FieldBinder.Bind(NormalizedView.Build(_raw), _allLabels))
            {
                var labelSpan = binding.LabelSpan;
                if ((labelSpan.Start > 0 && UsSsnScanner.IsAsciiAlphanumeric(_raw[labelSpan.Start - 1])) // [EA] a label is not inside a word
                    || (labelSpan.End < _raw.Length && UsSsnScanner.IsAsciiAlphanumeric(_raw[labelSpan.End])))
                {
                    continue;
                }

                UsTaxpayerKind? owner = _kindByLabel.TryGetValue(binding.Label.Value, out var taxKind) ? taxKind : null;
                UsTaxpayerKind? active = owner is { } o && Relevant(o) ? o : null;

                if (binding.State != FieldBindingState.FieldAccepted)
                {
                    if (active is { } a)
                    {
                        bool endedByNextLabel = binding.State == FieldBindingState.RejectedEmptyCandidate
                                                && binding.Terminator == FieldBindingTerminator.NextRecognizedLabel;
                        Reject(
                            a,
                            labelSpan,
                            endedByNextLabel
                                ? UsTaxpayerRejectionReason.NextRecognizedFieldTerminatesBinding
                                : UsTaxpayerRejectionReason.FieldBindingRejected,
                            labelled: true);
                    }

                    continue;
                }

                int start = binding.CandidateSpan.Start;
                int end = start + binding.CandidateSpan.Length;
                while (end > start && _raw[end - 1] == ' ') end--;
                if (end == start) continue;

                _regions.Add(new Region(binding.CandidateSpan, owner));
                if (active is { } label) ClassifyLabelled(label, start, end - start);
            }
        }

        private void ClassifyLabelled(UsTaxpayerKind kind, int start, int length)
        {
            var value = _raw.AsSpan(start, length);
            var shape = UsSsnScanner.GetShape(value);

            if (shape != UsSsnScanner.Shape.None)
            {
                AddWindowOrReject(new RawSpan(start, length), shape, kind);
                return;
            }

            // Not a clean number: a boundary violation, trailing content, a length defect or an unknown shape.
            if (value.Length > 9 && UsSsnScanner.GetShape(value[..9]) == UsSsnScanner.Shape.Compact)
            {
                RejectOuter(kind, start, 9, value[9]);
            }
            else if (value.Length > 11 && UsSsnScanner.GetShape(value[..11]) is UsSsnScanner.Shape.Dashed or UsSsnScanner.Shape.Spaced)
            {
                RejectOuter(kind, start, 11, value[11]);
            }
            else if (value.Length == 10 && UsSsnScanner.IsBoundaryChar(value[0])
                     && UsSsnScanner.GetShape(value[1..]) == UsSsnScanner.Shape.Compact)
            {
                Reject(kind, new RawSpan(start + 1, 9), UsTaxpayerRejectionReason.AlphanumericBoundaryViolation, labelled: true);
            }
            else if (value.Length == 12 && UsSsnScanner.IsBoundaryChar(value[0])
                     && UsSsnScanner.GetShape(value[1..]) is UsSsnScanner.Shape.Dashed or UsSsnScanner.Shape.Spaced)
            {
                Reject(kind, new RawSpan(start + 1, 11), UsTaxpayerRejectionReason.AlphanumericBoundaryViolation, labelled: true);
            }
            else
            {
                Reject(
                    kind,
                    new RawSpan(start, length),
                    LooksLikeGroupedDigits(value) ? UsTaxpayerRejectionReason.InvalidLength : UsTaxpayerRejectionReason.UnrecognizedValueShape,
                    labelled: true);
            }
        }

        // Three non-empty ASCII digit groups joined by two identical standard separators, but not 3-2-4.
        private static bool LooksLikeGroupedDigits(ReadOnlySpan<char> value)
        {
            char separator = '\0';
            int groups = 1, groupLength = 0;
            foreach (char c in value)
            {
                if (c >= '0' && c <= '9') { groupLength++; continue; }
                if ((c != '-' && c != ' ') || groupLength == 0 || (separator != '\0' && c != separator)) return false;
                separator = c;
                groups++;
                groupLength = 0;
            }

            return groups == 3 && groupLength > 0;
        }

        private void RejectOuter(UsTaxpayerKind kind, int start, int coreLength, char next) =>
            Reject(
                kind,
                new RawSpan(start, coreLength),
                UsSsnScanner.IsBoundaryChar(next)
                    ? UsTaxpayerRejectionReason.AlphanumericBoundaryViolation
                    : UsTaxpayerRejectionReason.TrailingContent,
                labelled: true);

        // ---- unlabelled DDD-DD-DDDD (structure only) ---------------------------------------------------------------

        public void CollectUnlabelledDashed()
        {
            for (int i = 0; i + 11 <= _raw.Length; i++)
            {
                if (!char.IsDigit(_raw[i]) || UsSsnScanner.GetShape(_raw.AsSpan(i, 11)) != UsSsnScanner.Shape.Dashed)
                {
                    continue;
                }

                var span = new RawSpan(i, 11);
                if (IsOwnedByAnotherField(span))
                {
                    i += 10;
                    continue;
                }

                bool touches = (i > 0 && UsSsnScanner.IsBoundaryChar(_raw[i - 1]))
                               || (i + 11 < _raw.Length && UsSsnScanner.IsBoundaryChar(_raw[i + 11]));
                if (touches)
                {
                    foreach (var kind in _kinds) Reject(kind, span, UsTaxpayerRejectionReason.AlphanumericBoundaryViolation, labelled: false);
                }
                else
                {
                    AddWindowOrReject(span, UsSsnScanner.Shape.Dashed, labelKind: null);
                }

                i += 10;
            }
        }

        // A number inside a recognized field's bound value belongs to that field. Family mode: only non-tax fields own it
        // (a value under a taxpayer label is shared by the family). Target mode: any field not owned by the target does.
        private bool IsOwnedByAnotherField(RawSpan span)
        {
            foreach (var region in _regions)
            {
                if (!region.Span.OverlapsWith(span)) continue;
                if (region.Owner is null) return true;
                if (_target is { } target && region.Owner != target) return true;
            }

            return false;
        }

        // ---- windows and per-type verdicts --------------------------------------------------------------------

        private void AddWindowOrReject(RawSpan span, UsSsnScanner.Shape shape, UsTaxpayerKind? labelKind)
        {
            var value = _raw.AsSpan(span.Start, span.Length);

            UsTaxpayerRejectionReason? defect = null;
            if (UsSsnScanner.HasNonAsciiDigit(value)) defect = UsTaxpayerRejectionReason.NonAsciiDigit; // [EA] before separators
            else if (shape == UsSsnScanner.Shape.UnsupportedSeparator) defect = UsTaxpayerRejectionReason.UnsupportedSeparator;
            else if (shape == UsSsnScanner.Shape.Mixed) defect = UsTaxpayerRejectionReason.MixedSeparators;

            if (defect is not null)
            {
                if (labelKind is { } owner) Reject(owner, span, defect.Value, labelled: true);
                else foreach (var kind in _kinds) Reject(kind, span, defect.Value, labelled: false);
                return;
            }

            var form = shape switch
            {
                UsSsnScanner.Shape.Compact => UsSsnForm.Compact,
                UsSsnScanner.Shape.Dashed => UsSsnForm.Dashed,
                _ => UsSsnForm.Spaced,
            };
            var window = new Window(span, form, UsSsnScanner.ExtractDigits(value, shape), labelKind);

            // A labelled window replaces an unlabelled one on the same span (it carries the context evidence).
            if (!_windows.TryGetValue(span, out var existing) || (existing.LabelKind is null && window.LabelKind is not null))
            {
                _windows[span] = window;
            }
        }

        private void Reject(UsTaxpayerKind kind, RawSpan span, UsTaxpayerRejectionReason reason, bool labelled) =>
            _rejected.Add(new UsTaxpayerAttempt(
                kind, span, labelled ? UsSsnEvidence.Structure | UsSsnEvidence.Context : UsSsnEvidence.Structure,
                UsTaxpayerAttemptOutcome.Rejected, reason));

        public UsTaxpayerFamilyResult Finish()
        {
            var attempts = new List<UsTaxpayerAttempt>(_rejected);
            var accepted = new List<UsTaxpayerCandidate>();

            foreach (var window in _windows.Values)
            {
                // Unlabelled non-dashed windows are never created, so a non-dashed window always has an owner label.
                var kinds = window.Form == UsSsnForm.Dashed ? _kinds : _kinds.Where(k => k == window.LabelKind).ToArray();
                foreach (var kind in kinds)
                {
                    var evidence = kind == window.LabelKind ? UsSsnEvidence.Structure | UsSsnEvidence.Context : UsSsnEvidence.Structure;
                    var reason = Validate(kind, window.Digits);
                    if (reason is null)
                    {
                        accepted.Add(new UsTaxpayerCandidate(kind, window.Span, evidence, window.Form, new UsSsnCanonical(window.Digits)));
                        attempts.Add(new UsTaxpayerAttempt(kind, window.Span, evidence, UsTaxpayerAttemptOutcome.Accepted, null));
                    }
                    else
                    {
                        attempts.Add(new UsTaxpayerAttempt(kind, window.Span, evidence, UsTaxpayerAttemptOutcome.Rejected, reason));
                    }
                }
            }

            var (emitted, superseded) = UsTaxpayerArbiter.Resolve(accepted);
            foreach (var candidate in superseded)
            {
                attempts.RemoveAll(a => a.Kind == candidate.Kind && a.Span == candidate.Span && a.Outcome == UsTaxpayerAttemptOutcome.Accepted);
                attempts.Add(new UsTaxpayerAttempt(candidate.Kind, candidate.Span, candidate.Evidence, UsTaxpayerAttemptOutcome.Superseded, null));
            }

            // A type that accepted a span carries no stale rejection for that same span (e.g. a trailing-content note).
            var acceptedKeys = attempts.Where(a => a.Outcome == UsTaxpayerAttemptOutcome.Accepted)
                .Select(a => (a.Kind, a.Span)).ToHashSet();
            var ordered = attempts
                .Where(a => a.Outcome != UsTaxpayerAttemptOutcome.Rejected || !acceptedKeys.Contains((a.Kind, a.Span)))
                .Distinct()
                .OrderBy(a => a.Span.Start).ThenBy(a => a.Span.Length).ThenBy(a => (int)a.Kind)
                .ThenBy(a => (int)a.Outcome).ThenBy(a => a.Reason)
                .ToList();

            return new UsTaxpayerFamilyResult(emitted, ordered);
        }

        private static UsTaxpayerRejectionReason? Validate(UsTaxpayerKind kind, string digits)
        {
            switch (kind)
            {
                case UsTaxpayerKind.Ssn:
                    return UsSsnScanner.RangeViolation(digits) switch
                    {
                        UsSsnRejectionReason.Area000 => UsTaxpayerRejectionReason.Area000,
                        UsSsnRejectionReason.Area666 => UsTaxpayerRejectionReason.Area666,
                        UsSsnRejectionReason.Area900To999 => UsTaxpayerRejectionReason.Area900To999,
                        UsSsnRejectionReason.Group00 => UsTaxpayerRejectionReason.Group00,
                        UsSsnRejectionReason.Serial0000 => UsTaxpayerRejectionReason.Serial0000,
                        _ => null,
                    };

                case UsTaxpayerKind.Itin:
                    if (digits[0] != '9') return UsTaxpayerRejectionReason.ItinAreaNotNine;
                    int itinGroup = int.Parse(digits.AsSpan(3, 2));
                    if (itinGroup < 50) return UsTaxpayerRejectionReason.ItinRangeBelow50;
                    if (itinGroup is >= 66 and <= 69) return UsTaxpayerRejectionReason.ItinRangeGap66To69;
                    if (itinGroup == 89) return UsTaxpayerRejectionReason.ItinRange89Excluded;
                    if (itinGroup == 93) return UsTaxpayerRejectionReason.ItinRange93Excluded;
                    return null;

                default: // Atin
                    if (digits[0] != '9') return UsTaxpayerRejectionReason.AtinAreaNotNine;
                    return digits.AsSpan(3, 2).SequenceEqual("93") ? null : UsTaxpayerRejectionReason.AtinRequires93;
            }
        }
    }
}
