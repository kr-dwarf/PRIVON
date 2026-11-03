using Privon.Detection;
using static Privon.UsMbiSevis.UsLabelledFields;

namespace Privon.UsMbiSevis;

// EXPERIMENT ONLY -- NOT AN OFFICIAL DETECTOR, NOT REGISTERED, NOT A SECURITY DETERMINATION. See UsMbiSevisTypes.cs.
//
// Frozen rules implemented (D2.2 grammar + D3A corpus, read-only): exactly ASCII 'N' followed by 10 ASCII digits;
// exact non-alphanumeric boundaries; Unicode lookalikes, embedded-token matches and wrong lengths fail. An exact valid
// shape is Level 3 with or without a SEVIS label (the earlier bare Level-1 path is removed by D2.2), so an unlabelled
// match is a candidate (evidence S); a match bound to a strong label carries S+C. Field binding is delegated to the
// shared FieldBinder.
//
// Everything marked [EA] is an experiment assumption for a branch the frozen corpus does not decide. Where a branch
// is undecided the code emits NO candidate (it never guesses a success).
//
// All spans are exact RAW spans: the grammar runs on the unmodified raw text (the shared normalizer folds fullwidth
// digits, which the frozen NON_ASCII_DIGIT fixture forbids).

public static class UsSevisScanner
{
    // The labels the contract lists for SEVIS. Longest first only for readability: FieldBinder prefers the longest
    // match regardless of order. The recognized-label set is an input by design.
    public static IReadOnlyList<string> DefaultStrongLabels { get; } = Array.AsReadOnly(["SEVIS ID", "SEVIS Number", "SEVIS"]);

    private const int DigitCount = 10;
    private const int ShapeLength = DigitCount + 1;

    public static UsSevisResult Detect(string? rawText) =>
        Detect(rawText, DefaultStrongLabels, Array.Empty<string>());

    /// <param name="strongLabels">Labels whose presence marks a field as SEVIS context.</param>
    /// <param name="otherRecognizedLabels">Labels of other fields; they only terminate a SEVIS field's candidate.</param>
    public static UsSevisResult Detect(
        string? rawText, IReadOnlyList<string> strongLabels, IReadOnlyList<string> otherRecognizedLabels)
    {
        ArgumentNullException.ThrowIfNull(strongLabels);
        ArgumentNullException.ThrowIfNull(otherRecognizedLabels);

        if (string.IsNullOrEmpty(rawText))
        {
            return UsSevisResult.Empty;
        }

        var candidates = new List<UsSevisCandidate>();
        var rejections = new List<UsSevisRejection>();

        foreach (var field in Read(rawText, strongLabels, otherRecognizedLabels))
        {
            if (!field.BindingAccepted)
            {
                rejections.Add(new UsSevisRejection(field.LabelSpan, UsSevisRejectionReason.FieldBindingRejected));
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
            .OrderBy(r => r.Span.Start).ThenBy(r => r.Span.Length).ThenBy(r => r.Reason)
            .ToList();

        return new UsSevisResult(byStart, distinctRejections);
    }

    // ---- unlabelled structure (Level 3 even without a label) -----------------------------------------------------

    // A token is a maximal run of letters and digits (ASCII or not): that is exactly the "exact non-alphanumeric
    // boundaries" the contract requires, and it makes non-ASCII neighbours count as part of the token.
    private static void ScanUnlabelled(string raw, List<UsSevisCandidate> candidates, List<UsSevisRejection> rejections)
    {
        int i = 0;
        while (i < raw.Length)
        {
            if (!char.IsLetterOrDigit(raw[i]))
            {
                i++;
                continue;
            }

            int end = i + 1;
            while (end < raw.Length && char.IsLetterOrDigit(raw[end]))
            {
                end++;
            }

            JudgeToken(raw, i, end - i, UsIdEvidence.Structure, candidates, rejections);
            i = end;
        }
    }

    // Returns true when the token is an exact SEVIS shape; otherwise records the reason when the token is a
    // recognisable near miss, and nothing for ordinary words and numbers.
    private static void JudgeToken(
        string raw, int start, int length, UsIdEvidence evidence,
        List<UsSevisCandidate> candidates, List<UsSevisRejection> rejections)
    {
        var token = raw.AsSpan(start, length);
        var span = new RawSpan(start, length);

        if (length == ShapeLength && IsCore(token))
        {
            candidates.Add(new UsSevisCandidate(span, evidence, new UsIdCanonical(token.ToString())));
            return;
        }

        // One letter followed by nine or more digits: a SEVIS attempt gone wrong. [EA] the first failing check wins:
        // length, then non-ASCII digits, then a non-ASCII prefix, then an ASCII prefix other than N.
        if (length >= DigitCount && char.IsLetter(token[0]) && AllDigits(token[1..]))
        {
            int digits = length - 1;
            if (digits != DigitCount)
            {
                rejections.Add(new UsSevisRejection(span, UsSevisRejectionReason.WrongLength));
            }
            else if (HasNonAsciiDigit(token[1..]))
            {
                rejections.Add(new UsSevisRejection(span, UsSevisRejectionReason.NonAsciiDigit));
            }
            else if (token[0] > 0x7F)
            {
                rejections.Add(new UsSevisRejection(span, UsSevisRejectionReason.NonAsciiLookalike));
            }
            else
            {
                rejections.Add(new UsSevisRejection(span, UsSevisRejectionReason.PrefixMustBeN));
            }

            return;
        }

        // A valid shape inside a longer token: the alphanumeric neighbour makes it a boundary violation.
        if (length > ShapeLength)
        {
            for (int k = 0; k + ShapeLength <= length; k++)
            {
                if (token[k] == 'N' && IsCore(token.Slice(k, ShapeLength)))
                {
                    rejections.Add(new UsSevisRejection(new RawSpan(start + k, ShapeLength), UsSevisRejectionReason.BoundaryViolation));
                    return;
                }
            }
        }
    }

    // ---- labelled value ------------------------------------------------------------------------------------------

    private static void ClassifyLabelled(
        string raw, RawSpan valueSpan, List<UsSevisCandidate> candidates, List<UsSevisRejection> rejections)
    {
        var value = raw.AsSpan(valueSpan.Start, valueSpan.Length);

        bool singleToken = true;
        foreach (char c in value)
        {
            if (!char.IsLetterOrDigit(c))
            {
                singleToken = false;
                break;
            }
        }

        if (singleToken)
        {
            int before = candidates.Count + rejections.Count;
            JudgeToken(raw, valueSpan.Start, valueSpan.Length, UsIdEvidence.Structure | UsIdEvidence.Context, candidates, rejections);
            if (candidates.Count + rejections.Count == before)
            {
                rejections.Add(new UsSevisRejection(valueSpan, UsSevisRejectionReason.UnrecognizedValueShape));
            }

            return;
        }

        // [EA] A clean SEVIS followed by more field text: the labelled field is not exactly the value. The structure
        // alone still matches (see ScanUnlabelled); the context is not credited.
        if (value.Length > ShapeLength && IsCore(value[..ShapeLength]) && !char.IsLetterOrDigit(value[ShapeLength]))
        {
            rejections.Add(new UsSevisRejection(valueSpan, UsSevisRejectionReason.TrailingContent));
            return;
        }

        rejections.Add(new UsSevisRejection(valueSpan, UsSevisRejectionReason.UnrecognizedValueShape));
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private static bool IsCore(ReadOnlySpan<char> eleven)
    {
        if (eleven.Length != ShapeLength || eleven[0] != 'N')
        {
            return false;
        }

        for (int k = 1; k < eleven.Length; k++)
        {
            if (!IsAsciiDigit(eleven[k])) return false;
        }

        return true;
    }

    private static bool AllDigits(ReadOnlySpan<char> value)
    {
        foreach (char c in value)
        {
            if (!char.IsDigit(c)) return false;
        }

        return true;
    }

    private static bool HasNonAsciiDigit(ReadOnlySpan<char> value)
    {
        foreach (char c in value)
        {
            if (!IsAsciiDigit(c)) return true;
        }

        return false;
    }
}
