namespace Privon.Detection;

/// <summary>
/// D3B0-A A.9 §7.3 -- ARCHITECTURE-LOCAL, NOT PARENT SEMANTICS. Domain:
/// <c>^[A-Za-z][A-Za-z0-9]*$</c>, ASCII only, first character a letter, maximum 64 characters,
/// case-SENSITIVE, ordinal equality, and NO normalization of any kind -- no Unicode
/// normalization, no case folding, no trimming, no substitution.
///
/// ASCII-only is deliberate: field names are schema identifiers, not content, so restricting
/// them removes the surrogate/normalization question entirely and keeps this type outside the
/// ValidUnicodeScalarSequence surface.
/// </summary>
public readonly record struct NormativeFieldName
{
    /// <summary>§7.3 maximum length; 65+ is invalid.</summary>
    public const int MaxLength = 64;

    private readonly string? _value;

    private NormativeFieldName(string value) => _value = value;

    /// <summary>The exact accepted string; <c>null</c> for <c>default</c>, which is INVALID.</summary>
    public string Value => _value!;

    /// <summary>
    /// DEFAULT_NORMATIVE_FIELD_NAME_IS_INVALID (§7.3.1): <c>default(NormativeFieldName)</c> is
    /// not a valid field name, never satisfies the domain, and is never accepted by
    /// <see cref="NormativeFieldBag.Create"/>. It exists ONLY as the failed-TryCreate out state.
    /// It is a value-type default state, NOT a semantic string sentinel.
    /// </summary>
    public bool HasValue => _value is not null;

    internal static bool IsValidName(string? value)
    {
        if (value is null) return false;
        if (value.Length == 0 || value.Length > MaxLength) return false;

        char first = value[0];
        bool firstOk = (first >= 'A' && first <= 'Z') || (first >= 'a' && first <= 'z');
        if (!firstOk) return false;

        for (int i = 1; i < value.Length; i++)
        {
            char c = value[i];
            bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>
    /// TRYCREATE_FAILURE_OUT_IS_DEFAULT (§7.3.1): on EVERY failure path, without exception,
    /// <c>name = default(NormativeFieldName)</c> is assigned BEFORE returning <c>false</c> --
    /// no alternate invalid object, no partially parsed value, no preserved input, no leftover
    /// from a previous call. Never throws, for any input including <c>null</c>. Pure: no static
    /// or cached state survives a call.
    /// </summary>
    public static bool TryCreate(string? value, out NormativeFieldName name)
    {
        // The single assignment below precedes every return false, so the observable is
        // identical whether the failure is detected at the first character or at character 65.
        name = default;
        if (!IsValidName(value)) return false;

        name = new NormativeFieldName(value!);
        return true;
    }

    /// <summary>Name only -- a schema identifier, never scanned content.</summary>
    public override string ToString() => _value ?? string.Empty;
}
