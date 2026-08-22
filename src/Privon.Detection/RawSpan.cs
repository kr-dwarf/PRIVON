namespace Privon.Detection;

/// <summary>
/// A character range in the original, unmodified RawText. Deliberately holds no text --
/// only a position -- so a span can be passed around and logged without ever carrying PII.
/// </summary>
public readonly record struct RawSpan(int Start, int Length)
{
    public int End => Start + Length;

    public bool OverlapsWith(RawSpan other) => Start < other.End && other.Start < End;
}
