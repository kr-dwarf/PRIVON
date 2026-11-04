namespace Privon.Detection;

/// <summary>D3B0-A A.9 §21.2 -- one physical line of the raw text.</summary>
public readonly record struct PhysicalLine(int Index, RawSpan Span, bool IsBlank);

/// <summary>
/// D3B0-A A.9 §21.2 -- generic physical-line model over the RAW text. Splits on CR, LF and CRLF.
/// <c>IsBlank</c> means the line's raw span contains no character other than ASCII space and HT.
/// No detector semantics, no label knowledge.
/// </summary>
public static class PhysicalLineIndex
{
    public static IReadOnlyList<PhysicalLine> Build(NormalizedView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        string raw = view.RawText;

        var lines = new List<PhysicalLine>();
        int start = 0, index = 0;
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c != '\r' && c != '\n') continue;

            lines.Add(Make(raw, index++, start, i - start));

            // CRLF counts as ONE terminator.
            if (c == '\r' && i + 1 < raw.Length && raw[i + 1] == '\n') i++;
            start = i + 1;
        }
        lines.Add(Make(raw, index, start, raw.Length - start));
        return lines;
    }

    private static PhysicalLine Make(string raw, int index, int start, int length)
    {
        bool blank = true;
        for (int i = start; i < start + length; i++)
            if (raw[i] != ' ' && raw[i] != '\t') { blank = false; break; }
        return new PhysicalLine(index, new RawSpan(start, length), blank);
    }

    /// <summary>The index of the line containing <paramref name="rawOffset"/>; -1 when none does.</summary>
    public static int LineIndexOf(IReadOnlyList<PhysicalLine> lines, int rawOffset)
    {
        ArgumentNullException.ThrowIfNull(lines);
        for (int i = 0; i < lines.Count; i++)
        {
            var s = lines[i].Span;
            if (rawOffset >= s.Start && rawOffset <= s.Start + s.Length) return lines[i].Index;
        }
        return -1;
    }
}
