namespace Privon.Detection;

/// <summary>
/// Maps a position/length in NormalizedView.Text back to the corresponding RawSpan in the
/// original RawText. Built by TextNormalizer alongside the normalized text itself.
/// </summary>
public sealed class RawIndexMap
{
    private readonly int[] _rawIndexOfNormalizedChar;

    internal RawIndexMap(int[] rawIndexOfNormalizedChar)
    {
        _rawIndexOfNormalizedChar = rawIndexOfNormalizedChar;
    }

    public RawSpan ToRawSpan(int normalizedStart, int normalizedLength)
    {
        if (normalizedLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(normalizedLength), "Span length must be positive.");
        if (normalizedStart < 0 || normalizedStart + normalizedLength > _rawIndexOfNormalizedChar.Length)
            throw new ArgumentOutOfRangeException(nameof(normalizedStart), "Span is outside the normalized text.");

        int rawStart = _rawIndexOfNormalizedChar[normalizedStart];
        int lastNormalizedIndex = normalizedStart + normalizedLength - 1;
        int rawEndExclusive = _rawIndexOfNormalizedChar[lastNormalizedIndex] + 1;
        return new RawSpan(rawStart, rawEndExclusive - rawStart);
    }
}
