using Privon.Detection;

namespace Privon.UsProductIntegration;

// ISOLATED PRODUCT-INTEGRATION CANDIDATE -- NOT REGISTERED, NOT ACTIVATED. See UsCandidateFactory.cs.
//
// Reads EVERY recognised field of a text through the shared FieldBinder (unchanged), with the same per-strong-label word rule as
// UsLabelledFields (a strong label sitting inside a longer word is not a label). The financial pairing needs the whole ordered
// list, because "no recognised field between" is a statement about neighbours.

/// <param name="Label">The catalog spelling of the matched label.</param>
/// <param name="ValueSpan">The bound value without trailing spaces (empty when nothing was bound).</param>
/// <param name="Accepted">The binding is valid (see <see cref="UsFieldReader.ReadAll"/> for the 96-character reading).</param>
internal readonly record struct UsReadField(
    string Label, RawSpan LabelSpan, RawSpan ValueSpan, bool Accepted, FieldBindingTerminator Terminator);

internal static class UsFieldReader
{
    /// <param name="measureAtTrimmedEnd">
    /// False: a field is valid exactly when the binder says so. True: the 96-raw-character limit is measured from the label start
    /// through the end of the VALUE rather than through the end of the trailing spaces the binder includes in its candidate. The
    /// frozen D3A row US-BANK-B128 (a routing field followed by 103 spaces) needs this reading; it is applied to the financial
    /// fields only, and a value that itself runs past the limit is still rejected.
    /// </param>
    public static IReadOnlyList<UsReadField> ReadAll(
        NormalizedView view, IReadOnlyList<string> labels, ISet<string> strongLabels, bool measureAtTrimmedEnd)
    {
        string raw = view.RawText;
        var all = new List<FieldLabel>();
        foreach (var text in labels)
        {
            if (FieldLabel.TryCreate(text, out var label) && !all.Contains(label))
            {
                all.Add(label);
            }
        }

        var fields = new List<UsReadField>();
        foreach (var binding in FieldBinder.Bind(view, all))
        {
            var labelSpan = binding.LabelSpan;
            if (strongLabels.Contains(binding.Label.Value)
                && ((labelSpan.Start > 0 && UsAscii.IsAlphanumeric(raw[labelSpan.Start - 1]))
                    || (labelSpan.End < raw.Length && UsAscii.IsAlphanumeric(raw[labelSpan.End]))))
            {
                continue; // a strong label inside a word is not a label
            }

            int start = binding.CandidateSpan.Start;
            int end = binding.CandidateSpan.End;
            while (end > start && raw[end - 1] == ' ')
            {
                end--;
            }

            bool accepted = binding.State == FieldBindingState.FieldAccepted
                || (measureAtTrimmedEnd
                    && binding.State == FieldBindingState.FieldRejectedOverMaxLength
                    && end > start
                    && end - labelSpan.Start <= FieldBinder.MaxLogicalFieldRawLength);

            fields.Add(new UsReadField(
                binding.Label.Value, labelSpan, new RawSpan(start, end - start), accepted && end > start, binding.Terminator));
        }

        return fields;
    }
}
