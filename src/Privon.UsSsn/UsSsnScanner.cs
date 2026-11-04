using Privon.Detection;

namespace Privon.UsSsn;

// EXPERIMENT ONLY -- NOT AN OFFICIAL DETECTOR, NOT REGISTERED, NOT A SECURITY DETERMINATION. See UsSsnTypes.cs.
//
// Frozen rules implemented (D3A corpus, read-only): 9 ASCII digits; DDD-DD-DDDD with no label (structure only);
// 9 digits or DDD-DD-DDDD or "DDD DD DDDD" after a strong label (structure + context); area 000 / 666 / 900-999,
// group 00 and serial 0000 rejected; '.' and other separators rejected; non-ASCII digits rejected (never folded);
// an alphanumeric neighbour is a boundary violation; unlabeled compact nine digits are NOT an SSN.
// Field binding (label, separator, terminators, 96-raw-character limit) is delegated to the shared FieldBinder.
//
// Everything marked [EA] is an experiment assumption for a branch the frozen corpus does not decide. Where a
// branch is undecided the code emits NO candidate (it never guesses a success).
//
// SSN / ITIN / ATIN exclusivity: ITIN and ATIN numbers begin with a 9xx area, which this module always rejects,
// so no SSN candidate can overlap an ITIN or ATIN one. ITIN/ATIN detection and the ATIN > ITIN > SSN arbitration
// are NOT implemented here.
//
// All spans are exact RAW spans: the grammar runs on the unmodified raw text (the shared normalizer folds
// fullwidth digits, which the frozen NON_ASCII_DIGIT fixture forbids).

public static class UsSsnScanner
{
    // Only the labels evidenced by the frozen fixtures. The recognized-label set is an input by design (FieldBinder is
    // grammar-free); further SSN label spellings are an open policy question, not guessed here.
    public static IReadOnlyList<string> DefaultStrongLabels { get; } = Array.AsReadOnly(["SSN", "Social Security Number"]);

    public static UsSsnScanResult Detect(string? rawText) =>
        Detect(rawText, DefaultStrongLabels, Array.Empty<string>());

    /// <param name="strongLabels">Labels whose presence marks a field as SSN context.</param>
    /// <param name="otherRecognizedLabels">Labels of other fields; they only terminate an SSN field's candidate.</param>
    public static UsSsnScanResult Detect(
        string? rawText, IReadOnlyList<string> strongLabels, IReadOnlyList<string> otherRecognizedLabels)
    {
        ArgumentNullException.ThrowIfNull(strongLabels);
        ArgumentNullException.ThrowIfNull(otherRecognizedLabels);

        if (string.IsNullOrEmpty(rawText))
        {
            return UsSsnScanResult.Empty;
        }

        var candidates = new List<UsSsnCandidate>();
        var rejections = new List<UsSsnRejection>();

        CollectLabelled(rawText, strongLabels, otherRecognizedLabels, candidates, rejections);
        CollectUnlabelledDashed(rawText, candidates, rejections);

        // One candidate per span (labelled, added first, wins); a span with a candidate carries no rejection.
        var byStart = candidates
            .GroupBy(c => c.Span)
            .Select(g => g.First())
            .OrderBy(c => c.Span.Start).ThenBy(c => c.Span.Length)
            .ToList();
        var candidateSpans = byStart.Select(c => c.Span).ToHashSet();
        var distinctRejections = rejections
            .Where(r => !candidateSpans.Contains(r.Span))
            .Distinct()
            .OrderBy(r => r.Span.Start).ThenBy(r => r.Span.Length).ThenBy(r => r.Reason)
            .ToList();

        return new UsSsnScanResult(byStart, distinctRejections);
    }

    // ---- labelled fields ------------------------------------------------------------------------------------------

    private static void CollectLabelled(
        string raw,
        IReadOnlyList<string> strongLabels,
        IReadOnlyList<string> otherLabels,
        List<UsSsnCandidate> candidates,
        List<UsSsnRejection> rejections)
    {
        var strong = new HashSet<string>(StringComparer.Ordinal);
        var all = new List<FieldLabel>();
        foreach (var text in strongLabels.Concat(otherLabels))
        {
            if (FieldLabel.TryCreate(text, out var label) && !all.Contains(label))
            {
                all.Add(label);
            }
        }

        foreach (var text in strongLabels)
        {
            if (!string.IsNullOrEmpty(text))
            {
                strong.Add(text);
            }
        }

        if (strong.Count == 0)
        {
            return;
        }

        foreach (var binding in FieldBinder.Bind(NormalizedView.Build(raw), all))
        {
            if (!strong.Contains(binding.Label.Value))
            {
                continue;
            }

            var labelSpan = binding.LabelSpan;
            if ((labelSpan.Start > 0 && IsAsciiAlphanumeric(raw[labelSpan.Start - 1])) // [EA] label must not sit inside a word
                || (labelSpan.End < raw.Length && IsAsciiAlphanumeric(raw[labelSpan.End])))
            {
                continue;
            }

            if (binding.State != FieldBindingState.FieldAccepted)
            {
                rejections.Add(new UsSsnRejection(labelSpan, UsSsnRejectionReason.FieldBindingRejected));
                continue;
            }

            int start = binding.CandidateSpan.Start;
            int end = binding.CandidateSpan.End;
            while (end > start && raw[end - 1] == ' ')
            {
                end--; // trailing spaces before a terminator are not part of the value
            }

            if (end > start)
            {
                Classify(raw, start, end - start, UsSsnEvidence.Structure | UsSsnEvidence.Context, candidates, rejections);
            }
        }
    }

    // ---- unlabelled DDD-DD-DDDD (structure only) ---------------------------------------------------------------

    private static void CollectUnlabelledDashed(string raw, List<UsSsnCandidate> candidates, List<UsSsnRejection> rejections)
    {
        for (int i = 0; i + 11 <= raw.Length; i++)
        {
            if (!char.IsDigit(raw[i]) || GetShape(raw.AsSpan(i, 11)) != Shape.Dashed)
            {
                continue;
            }

            bool touchesBefore = i > 0 && IsBoundaryChar(raw[i - 1]);
            bool touchesAfter = i + 11 < raw.Length && IsBoundaryChar(raw[i + 11]);
            if (touchesBefore || touchesAfter)
            {
                rejections.Add(new UsSsnRejection(new RawSpan(i, 11), UsSsnRejectionReason.AlphanumericBoundaryViolation));
            }
            else
            {
                Classify(raw, i, 11, UsSsnEvidence.Structure, candidates, rejections);
            }

            i += 10;
        }
    }

    // ---- value classification ------------------------------------------------------------------------------------

    internal enum Shape { None, Compact, Dashed, Spaced, Mixed, UnsupportedSeparator }

    private static void Classify(
        string raw, int start, int length, UsSsnEvidence evidence,
        List<UsSsnCandidate> candidates, List<UsSsnRejection> rejections)
    {
        var value = raw.AsSpan(start, length);
        var shape = GetShape(value);

        if (shape != Shape.None)
        {
            Judge(raw, start, length, shape, evidence, candidates, rejections);
            return;
        }

        // Not a clean number: decide between a boundary violation and other content around a number-shaped core.
        if (value.Length > 9 && GetShape(value[..9]) == Shape.Compact)
        {
            AddOuterReason(rejections, start, 9, value[9]);
        }
        else if (value.Length > 11 && GetShape(value[..11]) is Shape.Dashed or Shape.Spaced)
        {
            AddOuterReason(rejections, start, 11, value[11]);
        }
        else if (value.Length == 10 && IsBoundaryChar(value[0]) && GetShape(value[1..]) == Shape.Compact)
        {
            rejections.Add(new UsSsnRejection(new RawSpan(start + 1, 9), UsSsnRejectionReason.AlphanumericBoundaryViolation));
        }
        else if (value.Length == 12 && IsBoundaryChar(value[0]) && GetShape(value[1..]) is Shape.Dashed or Shape.Spaced)
        {
            rejections.Add(new UsSsnRejection(new RawSpan(start + 1, 11), UsSsnRejectionReason.AlphanumericBoundaryViolation));
        }
        else
        {
            rejections.Add(new UsSsnRejection(new RawSpan(start, length), UsSsnRejectionReason.UnrecognizedValueShape));
        }
    }

    private static void AddOuterReason(List<UsSsnRejection> rejections, int start, int coreLength, char next) =>
        rejections.Add(new UsSsnRejection(
            new RawSpan(start, coreLength),
            IsBoundaryChar(next) ? UsSsnRejectionReason.AlphanumericBoundaryViolation : UsSsnRejectionReason.TrailingContent));

    private static void Judge(
        string raw, int start, int length, Shape shape, UsSsnEvidence evidence,
        List<UsSsnCandidate> candidates, List<UsSsnRejection> rejections)
    {
        var span = new RawSpan(start, length);
        var value = raw.AsSpan(start, length);

        if (HasNonAsciiDigit(value))
        {
            rejections.Add(new UsSsnRejection(span, UsSsnRejectionReason.NonAsciiDigit)); // [EA] checked before separators
            return;
        }

        if (shape == Shape.UnsupportedSeparator)
        {
            rejections.Add(new UsSsnRejection(span, UsSsnRejectionReason.UnsupportedSeparator));
            return;
        }

        if (shape == Shape.Mixed)
        {
            rejections.Add(new UsSsnRejection(span, UsSsnRejectionReason.MixedSeparators));
            return;
        }

        string digits = ExtractDigits(value, shape);
        var invalid = RangeViolation(digits);
        if (invalid is not null)
        {
            rejections.Add(new UsSsnRejection(span, invalid.Value));
            return;
        }

        var form = shape switch
        {
            Shape.Compact => UsSsnForm.Compact,
            Shape.Dashed => UsSsnForm.Dashed,
            _ => UsSsnForm.Spaced,
        };
        candidates.Add(new UsSsnCandidate(span, evidence, form, new UsSsnCanonical(digits)));
    }

    // Area / group / serial, first failing rule wins [EA ordering].
    internal static UsSsnRejectionReason? RangeViolation(string digits)
    {
        int area = int.Parse(digits.AsSpan(0, 3));
        if (area == 0) return UsSsnRejectionReason.Area000;
        if (area == 666) return UsSsnRejectionReason.Area666;
        if (area >= 900) return UsSsnRejectionReason.Area900To999;
        if (digits.AsSpan(3, 2).SequenceEqual("00")) return UsSsnRejectionReason.Group00;
        if (digits.AsSpan(5, 4).SequenceEqual("0000")) return UsSsnRejectionReason.Serial0000;
        return null;
    }

    internal static Shape GetShape(ReadOnlySpan<char> value)
    {
        if (value.Length == 9)
        {
            return AllDigits(value) ? Shape.Compact : Shape.None;
        }

        if (value.Length != 11
            || !AllDigits(value[..3]) || !AllDigits(value.Slice(4, 2)) || !AllDigits(value.Slice(7, 4)))
        {
            return Shape.None;
        }

        char first = value[3];
        char second = value[6];
        if (first == '-' && second == '-') return Shape.Dashed;
        if (first == ' ' && second == ' ') return Shape.Spaced;
        if (IsStandardSeparator(first) && IsStandardSeparator(second)) return Shape.Mixed;
        if (!char.IsLetterOrDigit(first) && !char.IsLetterOrDigit(second)) return Shape.UnsupportedSeparator;
        return Shape.None;
    }

    internal static string ExtractDigits(ReadOnlySpan<char> value, Shape shape)
    {
        Span<char> digits = stackalloc char[9];
        int n = 0;
        foreach (char c in value)
        {
            if (c >= '0' && c <= '9')
            {
                digits[n++] = c;
            }
        }

        return new string(digits[..n]);
    }

    private static bool AllDigits(ReadOnlySpan<char> value)
    {
        foreach (char c in value)
        {
            if (!char.IsDigit(c)) return false;
        }

        return true;
    }

    internal static bool HasNonAsciiDigit(ReadOnlySpan<char> value)
    {
        foreach (char c in value)
        {
            if (char.IsDigit(c) && (c < '0' || c > '9')) return true;
        }

        return false;
    }

    private static bool IsStandardSeparator(char c) => c == '-' || c == ' ';

    internal static bool IsAsciiAlphanumeric(char c) =>
        (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

    // [EA] A neighbouring letter, any Unicode digit or a hyphen means the number is part of something larger.
    internal static bool IsBoundaryChar(char c) => char.IsLetterOrDigit(c) || c == '-';
}
