namespace Privon.Detection;

/// <summary>
/// A detection-only normalized rendering of RawText, paired with the RawIndexMap needed to
/// translate any match position back to the original text. RawText itself is untouched --
/// this is always a separate string built by TextNormalizer.
/// </summary>
public sealed class NormalizedView
{
    public string Text { get; }
    public RawIndexMap IndexMap { get; }

    /// <summary>
    /// The original, unmodified input string. Detectors that must recover the exact raw
    /// characters within a span -- e.g. Secret, where a zero-width character stripped by
    /// normalization might be a real credential character rather than noise -- slice this
    /// directly instead of using a detector's matched (normalized) text.
    /// </summary>
    public string RawText { get; }

    private NormalizedView(string rawText, string text, RawIndexMap indexMap)
    {
        RawText = rawText;
        Text = text;
        IndexMap = indexMap;
    }

    public static NormalizedView Build(string rawText)
    {
        ArgumentNullException.ThrowIfNull(rawText);
        var (text, map) = TextNormalizer.Normalize(rawText);
        return new NormalizedView(rawText, text, map);
    }
}
