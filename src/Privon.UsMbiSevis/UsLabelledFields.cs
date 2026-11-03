using Privon.Detection;

namespace Privon.UsMbiSevis;

// EXPERIMENT ONLY -- NOT AN OFFICIAL DETECTOR, NOT REGISTERED, NOT A SECURITY DETERMINATION. See UsMbiSevisTypes.cs.
//
// Field binding (label, separator, terminators, 96-raw-character limit, next-label termination) is delegated to the
// shared, grammar-free FieldBinder; this file only adapts its output. The recognized-label set is an input by design.
// Everything marked [EA] is an experiment assumption for a branch the frozen corpus does not decide.

/// <summary>One strong-label field: the label span, and the value span with trailing spaces removed.</summary>
internal readonly record struct UsLabelledField(RawSpan LabelSpan, RawSpan ValueSpan, bool BindingAccepted);

internal static class UsLabelledFields
{
    internal static List<UsLabelledField> Read(
        string raw, IReadOnlyList<string> strongLabels, IReadOnlyList<string> otherLabels)
    {
        var fields = new List<UsLabelledField>();

        var strong = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in strongLabels)
        {
            if (!string.IsNullOrEmpty(text))
            {
                strong.Add(text);
            }
        }

        if (strong.Count == 0)
        {
            return fields;
        }

        var all = new List<FieldLabel>();
        foreach (var text in strongLabels.Concat(otherLabels))
        {
            if (FieldLabel.TryCreate(text, out var label) && !all.Contains(label))
            {
                all.Add(label);
            }
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
                fields.Add(new UsLabelledField(labelSpan, default, false));
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
                fields.Add(new UsLabelledField(labelSpan, new RawSpan(start, end - start), true));
            }
        }

        return fields;
    }

    internal static bool IsAsciiAlphanumeric(char c) =>
        (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

    internal static bool IsAsciiDigit(char c) => c >= '0' && c <= '9';

    // ASCII-only case folding: the dotless-i / Kelvin-sign problem must not be reachable from a grammar rule.
    internal static char AsciiUpper(char c) => c >= 'a' && c <= 'z' ? (char)(c - 32) : c;

    // [EA] Boundary: any letter or digit, ASCII or not, touches the identifier. Contract-literal: a hyphen or underscore
    // is a non-alphanumeric boundary (unlike the SSN module, which also treats a hyphen as part of a larger number).
    internal static bool IsBoundaryAlphanumeric(char c) => char.IsLetterOrDigit(c);
}
