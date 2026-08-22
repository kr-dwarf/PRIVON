namespace Privon.Core;

/// <summary>
/// Monotonically increasing identifier for a composer/clipboard editing state. Never
/// carries any PII -- it is just a counter.
/// </summary>
public readonly record struct RevisionId(long Value)
{
    public static readonly RevisionId None = new(0);

    public override string ToString() => Value.ToString();
}
