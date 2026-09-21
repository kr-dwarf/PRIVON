namespace Privon.Detection;

/// <summary>D3B0-A A.9 §21.1 -- a caller-supplied recognized label. The grammar never hard-codes one.</summary>
public readonly record struct FieldLabel
{
    private readonly string? _value;
    private FieldLabel(string value) => _value = value;

    public string Value => _value!;
    public bool HasValue => _value is not null;

    public static bool TryCreate(string? value, out FieldLabel label)
    {
        label = default;
        if (string.IsNullOrEmpty(value)) return false;
        label = new FieldLabel(value);
        return true;
    }

    public override string ToString() => _value ?? string.Empty;
}

/// <summary>D3B0-A A.9 §21.1 -- what ended a logical field's binding.</summary>
public enum FieldBindingTerminator
{
    Cr, Lf, Semicolon, HorizontalTab, VerticalBar, NextRecognizedLabel, EndOfText,
}

/// <summary>D3B0-A A.9 §21.1 -- the outcome of one binding attempt.</summary>
public enum FieldBindingState
{
    FieldAccepted,
    FieldRejectedOverMaxLength,
    RejectedPreSeparatorSpaces,
    RejectedPostSeparatorSpaces,
    RejectedNoLabelMatch,
    RejectedEmptyCandidate,
}

/// <summary>D3B0-A A.9 §21.1 -- one bound logical field; all three spans are EXACT RAW spans.</summary>
public sealed record FieldBinding(
    FieldLabel Label, RawSpan LabelSpan, RawSpan CandidateSpan, RawSpan LogicalFieldSpan,
    char? Separator, int PreSeparatorSpaces, int PostSeparatorSpaces,
    FieldBindingTerminator Terminator, FieldBindingState State);

/// <summary>
/// D3B0-A A.9 §21.1 -- the shared logical-field binding primitive, expressed as VERBATIM D2.2
/// grammar.
///
/// FIELD_BINDER_IS_GRAMMAR_FREE: no U.S.- or Korean-specific label list, value grammar, checksum,
/// range rule, or jurisdiction rule. The recognized-label set is ALWAYS an input parameter. No
/// reference to any DetectorId, to DetectorRegistry, or to any authority snapshot.
///
/// The superseded "within 32 raw characters" wording is FORBIDDEN; the limit is 96 raw characters
/// from label start through candidate end (96 accepted, 97 rejected).
/// </summary>
public static class FieldBinder
{
    public const int MaxLogicalFieldRawLength = 96;

    private const int MaxSeparatorAdjacentSpaces = 3;   // 0-3 accepted; 4+ rejected

    public static IReadOnlyList<FieldBinding> Bind(NormalizedView view, IReadOnlyList<FieldLabel> recognizedLabels)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(recognizedLabels);

        string raw = view.RawText;
        var results = new List<FieldBinding>();

        for (int i = 0; i < raw.Length; i++)
        {
            if (!TryMatchLabel(raw, i, recognizedLabels, out var label, out int labelLength)) continue;

            int cursor = i + labelLength;

            // 0-3 ASCII U+0020 after the label; 4+ => RejectedPreSeparatorSpaces.
            int pre = CountSpaces(raw, cursor);
            if (pre > MaxSeparatorAdjacentSpaces)
            {
                results.Add(Rejected(label, i, labelLength, FieldBindingState.RejectedPreSeparatorSpaces, pre, 0));
                i = cursor - 1;
                continue;
            }
            cursor += pre;

            // Either no separator, or exactly one of ':', '#', '='.
            char? separator = null;
            if (cursor < raw.Length && (raw[cursor] == ':' || raw[cursor] == '#' || raw[cursor] == '='))
            {
                separator = raw[cursor];
                cursor++;
            }

            // 0-3 ASCII U+0020 after it; 4+ => RejectedPostSeparatorSpaces.
            int post = CountSpaces(raw, cursor);
            if (post > MaxSeparatorAdjacentSpaces)
            {
                results.Add(Rejected(label, i, labelLength, FieldBindingState.RejectedPostSeparatorSpaces, pre, post));
                i = cursor - 1;
                continue;
            }
            cursor += post;

            // The candidate starts IMMEDIATELY -- no intervening word, sentence fragment or
            // arbitrary punctuation is skipped over here.
            int candidateStart = cursor;

            // Terminate at the EARLIEST of CR, LF, ';', HT, '|', the start of another recognized
            // label, or end of text.
            var terminator = FieldBindingTerminator.EndOfText;
            int end = raw.Length;
            for (int j = candidateStart; j < raw.Length; j++)
            {
                char c = raw[j];
                if (c == '\r') { terminator = FieldBindingTerminator.Cr; end = j; break; }
                if (c == '\n') { terminator = FieldBindingTerminator.Lf; end = j; break; }
                if (c == ';') { terminator = FieldBindingTerminator.Semicolon; end = j; break; }
                if (c == '\t') { terminator = FieldBindingTerminator.HorizontalTab; end = j; break; }
                if (c == '|') { terminator = FieldBindingTerminator.VerticalBar; end = j; break; }
                if (j > candidateStart && TryMatchLabel(raw, j, recognizedLabels, out _, out _))
                { terminator = FieldBindingTerminator.NextRecognizedLabel; end = j; break; }
            }

            int candidateLength = end - candidateStart;
            var labelSpan = new RawSpan(i, labelLength);
            var candidateSpan = new RawSpan(candidateStart, candidateLength);
            int logicalLength = end - i;
            var logicalSpan = new RawSpan(i, logicalLength);

            var state =
                candidateLength <= 0 ? FieldBindingState.RejectedEmptyCandidate
                : logicalLength > MaxLogicalFieldRawLength ? FieldBindingState.FieldRejectedOverMaxLength
                : FieldBindingState.FieldAccepted;

            results.Add(new FieldBinding(
                label, labelSpan, candidateSpan, logicalSpan, separator, pre, post, terminator, state));

            i = Math.Max(i, end - 1);
        }

        return results;
    }

    // ASCII-case-insensitive label match. Deliberately not ToUpper/ToLower or a culture-aware
    // comparison: the Turkish dotless-i problem must not be reachable from a grammar rule.
    private static bool TryMatchLabel(
        string raw, int at, IReadOnlyList<FieldLabel> labels, out FieldLabel matched, out int length)
    {
        matched = default;
        length = 0;

        // Longest match wins, so a label that is a prefix of another cannot shadow it.
        for (int k = 0; k < labels.Count; k++)
        {
            var label = labels[k];
            if (!label.HasValue) continue;
            string text = label.Value;
            if (text.Length == 0 || at + text.Length > raw.Length) continue;

            bool ok = true;
            for (int t = 0; t < text.Length; t++)
                if (AsciiLower(raw[at + t]) != AsciiLower(text[t])) { ok = false; break; }

            if (ok && text.Length > length) { matched = label; length = text.Length; }
        }
        return length > 0;
    }

    private static char AsciiLower(char c) => c >= 'A' && c <= 'Z' ? (char)(c + 32) : c;

    private static int CountSpaces(string raw, int at)
    {
        int n = 0;
        while (at + n < raw.Length && raw[at + n] == ' ') n++;
        return n;
    }

    private static FieldBinding Rejected(
        FieldLabel label, int labelStart, int labelLength, FieldBindingState state, int pre, int post) =>
        new(label, new RawSpan(labelStart, labelLength), new RawSpan(labelStart + labelLength, 0),
            new RawSpan(labelStart, labelLength), null, pre, post,
            FieldBindingTerminator.EndOfText, state);
}
