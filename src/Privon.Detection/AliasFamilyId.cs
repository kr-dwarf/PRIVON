namespace Privon.Detection;

/// <summary>
/// D3B0-A A.9 §22.2 -- a language-neutral alias family, 1:1 with a semantic type. Derived from
/// <see cref="PiiType"/> through the frozen table of §22.3 (see <see cref="AliasFamilyTable"/>)
/// -- NEVER from <see cref="AliasLabelProvider"/>'s Korean noun and never from any localized
/// string (LOCALIZED_LITERAL_IS_NOT_SEMANTIC_IDENTITY).
/// </summary>
public readonly record struct AliasFamilyId
{
    private readonly string? _value;

    internal AliasFamilyId(string value) => _value = value;

    public string Value => _value!;

    public bool HasValue => _value is not null;

    public override string ToString() => _value ?? string.Empty;
}
