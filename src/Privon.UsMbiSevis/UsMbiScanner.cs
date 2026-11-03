using Privon.Detection;
using static Privon.UsMbiSevis.UsLabelledFields;

namespace Privon.UsMbiSevis;

// EXPERIMENT ONLY -- NOT AN OFFICIAL DETECTOR, NOT REGISTERED, NOT A SECURITY DETERMINATION. See UsMbiSevisTypes.cs.
//
// Frozen rules implemented (D2.2 grammar + D3A corpus, read-only): exactly 11 characters with the CMS position rules
//   1 [1-9]   2 [ACDEFGHJKMNPQRTUVWXY]   3 [0-9 or allowed letter]   4 [0-9]   5 [allowed letter]
//   6 [0-9 or allowed letter]   7 [0-9]   8-9 [allowed letter]   10-11 [0-9];
// either 11 contiguous characters or 4-3-4 display grouping with ONE consistent ASCII hyphen or ASCII space; ASCII
// letters are case-normalised; exact non-alphanumeric boundaries; non-ASCII lookalikes are never folded. A valid MBI
// is Level 3 with or without a label, so an unlabelled structural match is a candidate (evidence S); a match bound
// to a strong label carries S+C. Field binding is delegated to the shared FieldBinder.
//
// Everything marked [EA] is an experiment assumption for a branch the frozen corpus does not decide. Where a branch
// is undecided the code emits NO candidate (it never guesses a success).
//
// All spans are exact RAW spans: the grammar runs on the unmodified raw text (the shared normalizer folds fullwidth
// characters, which the frozen NON_ASCII_LOOKALIKE fixture forbids).

public static class UsMbiScanner
{
    // Only the label evidenced by the frozen fixtures. The recognized-label set is an input by design (FieldBinder is
    // grammar-free); further MBI label spellings (for example the CMS long name) are an open policy question.
    public static IReadOnlyList<string> DefaultStrongLabels { get; } = Array.AsReadOnly(["MBI"]);

    private const string AllowedLetters = "ACDEFGHJKMNPQRTUVWXY";

    // Position kinds, 1-based order: 1 = [1-9], L = allowed letter, A = digit or allowed letter, D = digit.
    private const string PositionKinds = "1LADLADLLDD";

    public static UsMbiResult Detect(string? rawText) =>
        Detect(rawText, DefaultStrongLabels, Array.Empty<string>());

    /// <param name="strongLabels">Labels whose presence marks a field as MBI context.</param>
    /// <param name="otherRecognizedLabels">Labels of other fields; they only terminate an MBI field's candidate.</param>
    public static UsMbiResult Detect(
        string? rawText, IReadOnlyList<string> strongLabels, IReadOnlyList<string> otherRecognizedLabels)
    {
        ArgumentNullException.ThrowIfNull(strongLabels);
        ArgumentNullException.ThrowIfNull(otherRecognizedLabels);

        if (string.IsNullOrEmpty(rawText))
        {
            return UsMbiResult.Empty;
        }

        var candidates = new List<UsMbiCandidate>();
        var rejections = new List<UsMbiRejection>();

        foreach (var field in Read(rawText, strongLabels, otherRecognizedLabels))
        {
            if (!field.BindingAccepted)
            {
                rejections.Add(new UsMbiRejection(field.LabelSpan, UsMbiRejectionReason.FieldBindingRejected));
                continue;
            }

            ClassifyLabelled(rawText, field.ValueSpan, candidates, rejections);
        }

        ScanUnlabelled(rawText, candidates, rejections);

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
            .OrderBy(r => r.Span.Start).ThenBy(r => r.Span.Length).ThenBy(r => r.Reason).ThenBy(r => r.Position)
            .ToList();

        return new UsMbiResult(byStart, distinctRejections);
    }

    // ---- unlabelled structure (Level 3 even without a label) -----------------------------------------------------

    private static void ScanUnlabelled(string raw, List<UsMbiCandidate> candidates, List<UsMbiRejection> rejections)
    {
        Span<char> eleven = stackalloc char[11];

        for (int i = 0; i < raw.Length; i++)
        {
            char first = raw[i];
            if (first < '1' || first > '9')
            {
                continue; // a valid MBI always starts with a digit 1-9
            }

            if (!TryMatchCoreAt(raw, i, eleven, out int length, out var form))
            {
                continue;
            }

            bool touchesBefore = i > 0 && IsBoundaryAlphanumeric(raw[i - 1]);
            bool touchesAfter = i + length < raw.Length && IsBoundaryAlphanumeric(raw[i + length]);
            var span = new RawSpan(i, length);
            if (touchesBefore || touchesAfter)
            {
                rejections.Add(new UsMbiRejection(span, UsMbiRejectionReason.BoundaryViolation));
            }
            else
            {
                candidates.Add(new UsMbiCandidate(span, UsIdEvidence.Structure, form, new UsIdCanonical(new string(eleven))));
            }

            i += length - 1;
        }
    }

    /// <summary>A complete, valid MBI (compact or 4-3-4) starting exactly at <paramref name="at"/>; no boundary check.</summary>
    private static bool TryMatchCoreAt(string raw, int at, Span<char> eleven, out int length, out UsMbiForm form)
    {
        length = 0;
        form = UsMbiForm.Compact;

        if (at + 11 <= raw.Length && AllAsciiAlphanumeric(raw.AsSpan(at, 11)))
        {
            Upper(raw.AsSpan(at, 11), eleven);
            if (CheckPositions(eleven, out _, out _))
            {
                length = 11;
                return true;
            }

            return false;
        }

        if (at + 13 <= raw.Length)
        {
            char separator = raw[at + 4];
            if ((separator == '-' || separator == ' ') && raw[at + 8] == separator
                && AllAsciiAlphanumeric(raw.AsSpan(at, 4)) && AllAsciiAlphanumeric(raw.AsSpan(at + 5, 3))
                && AllAsciiAlphanumeric(raw.AsSpan(at + 9, 4)))
            {
                Gather(raw.AsSpan(at, 13), eleven);
                if (CheckPositions(eleven, out _, out _))
                {
                    length = 13;
                    form = separator == '-' ? UsMbiForm.Hyphenated : UsMbiForm.Spaced;
                    return true;
                }
            }
        }

        return false;
    }

    // ---- labelled value ------------------------------------------------------------------------------------------

    private static void ClassifyLabelled(
        string raw, RawSpan valueSpan, List<UsMbiCandidate> candidates, List<UsMbiRejection> rejections)
    {
        var value = raw.AsSpan(valueSpan.Start, valueSpan.Length);
        int alnum = 0, other = 0;
        bool nonAscii = false;
        foreach (char c in value)
        {
            if (IsAsciiAlphanumeric(c)) alnum++;
            else if (char.IsLetterOrDigit(c)) nonAscii = true;
            else other++;
        }

        void Reject(UsMbiRejectionReason reason, int? position = null) =>
            rejections.Add(new UsMbiRejection(valueSpan, reason, position));

        if (nonAscii)
        {
            // [EA] checked before separators and positions, mirroring the SSN module's NonAsciiDigit ordering.
            Reject(valueSpan.Length is 11 or 13 ? UsMbiRejectionReason.NonAsciiLookalike : UsMbiRejectionReason.UnrecognizedValueShape);
            return;
        }

        // [EA] A clean MBI followed by more field text: the labelled field is not exactly the value. The structure
        // alone still matches (see ScanUnlabelled); the context is not credited.
        Span<char> eleven = stackalloc char[11];
        if (value.Length > 11 && TryMatchCoreAt(raw, valueSpan.Start, eleven, out int coreLength, out _)
            && value.Length > coreLength && !IsAsciiAlphanumeric(value[coreLength]))
        {
            Reject(UsMbiRejectionReason.TrailingContent);
            return;
        }

        if (other == 0)
        {
            if (alnum != 11)
            {
                Reject(UsMbiRejectionReason.WrongLength);
                return;
            }

            Judge(value, valueSpan, UsMbiForm.Compact, candidates, rejections);
            return;
        }

        if (other == 2)
        {
            int first = -1, second = -1;
            for (int k = 0; k < value.Length; k++)
            {
                if (IsAsciiAlphanumeric(value[k])) continue;
                if (first < 0) first = k; else second = k;
            }

            if (alnum != 11)
            {
                Reject(UsMbiRejectionReason.WrongLength);
                return;
            }

            bool standard = IsStandardSeparator(value[first]) && IsStandardSeparator(value[second]);
            if (first != 4 || second != 8)
            {
                Reject(standard ? UsMbiRejectionReason.BadGrouping : UsMbiRejectionReason.UnsupportedSeparator);
            }
            else if (!standard)
            {
                Reject(UsMbiRejectionReason.UnsupportedSeparator);
            }
            else if (value[first] != value[second])
            {
                Reject(UsMbiRejectionReason.MixedSeparators);
            }
            else
            {
                Judge(value, valueSpan, value[first] == '-' ? UsMbiForm.Hyphenated : UsMbiForm.Spaced, candidates, rejections);
            }

            return;
        }

        Reject(UsMbiRejectionReason.UnrecognizedValueShape);
    }

    private static void Judge(
        ReadOnlySpan<char> value, RawSpan valueSpan, UsMbiForm form,
        List<UsMbiCandidate> candidates, List<UsMbiRejection> rejections)
    {
        Span<char> eleven = stackalloc char[11];
        Gather(value, eleven);

        if (!CheckPositions(eleven, out var reason, out int position))
        {
            rejections.Add(new UsMbiRejection(valueSpan, reason, position)); // [EA] the first failing position wins
            return;
        }

        candidates.Add(new UsMbiCandidate(
            valueSpan, UsIdEvidence.Structure | UsIdEvidence.Context, form, new UsIdCanonical(new string(eleven))));
    }

    // ---- position rules ------------------------------------------------------------------------------------------

    private static bool CheckPositions(ReadOnlySpan<char> eleven, out UsMbiRejectionReason reason, out int position)
    {
        for (int p = 0; p < 11; p++)
        {
            char c = eleven[p];
            bool digit = IsAsciiDigit(c);
            bool allowedLetter = AllowedLetters.Contains(c);
            bool ok;
            UsMbiRejectionReason failure;

            switch (PositionKinds[p])
            {
                case '1':
                    ok = c >= '1' && c <= '9';
                    failure = UsMbiRejectionReason.Position1RequiresOneToNine;
                    break;
                case 'D':
                    ok = digit;
                    failure = UsMbiRejectionReason.PositionRequiresDigit;
                    break;
                case 'L':
                    ok = allowedLetter;
                    failure = digit ? UsMbiRejectionReason.PositionRequiresAllowedLetter : Excluded(c);
                    break;
                default: // 'A'
                    ok = digit || allowedLetter;
                    failure = Excluded(c);
                    break;
            }

            if (!ok)
            {
                reason = failure;
                position = p + 1;
                return false;
            }
        }

        reason = default;
        position = 0;
        return true;
    }

    // The 26 ASCII letters are exactly the 20 allowed ones plus these six, so a non-allowed letter is one of them.
    private static UsMbiRejectionReason Excluded(char upper) => upper switch
    {
        'B' => UsMbiRejectionReason.ExcludedB,
        'I' => UsMbiRejectionReason.ExcludedI,
        'L' => UsMbiRejectionReason.ExcludedL,
        'O' => UsMbiRejectionReason.ExcludedO,
        'S' => UsMbiRejectionReason.ExcludedS,
        _ => UsMbiRejectionReason.ExcludedZ,
    };

    // ---- helpers -------------------------------------------------------------------------------------------------

    private static bool IsStandardSeparator(char c) => c == '-' || c == ' ';

    private static bool AllAsciiAlphanumeric(ReadOnlySpan<char> value)
    {
        foreach (char c in value)
        {
            if (!IsAsciiAlphanumeric(c)) return false;
        }

        return true;
    }

    private static void Upper(ReadOnlySpan<char> source, Span<char> destination)
    {
        for (int k = 0; k < source.Length; k++)
        {
            destination[k] = AsciiUpper(source[k]);
        }
    }

    /// <summary>Copies the 11 alphanumeric characters of a compact or 4-3-4 display value, upper-cased.</summary>
    private static void Gather(ReadOnlySpan<char> display, Span<char> eleven)
    {
        int n = 0;
        foreach (char c in display)
        {
            if (IsAsciiAlphanumeric(c))
            {
                eleven[n++] = AsciiUpper(c);
            }
        }
    }
}
