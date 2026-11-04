namespace Privon.Detection.Scope;

/// <summary>
/// D3B0-A A.17 §16.7.1 -- a canonical scope query identity.
///
/// SCOPE_IDENTITY_IS_NOT_A_DETECTOR_ID: this type shares the §5.1 lexical grammar with
/// <see cref="DetectorId"/> but is a different type. It is never a registry key, carries no rank,
/// and implies neither that a detector exists nor that one is registered or Ready. No conversion
/// between the two types exists.
///
/// Equality is ordinal and case-sensitive. Construction never trims, case-folds, transliterates,
/// normalizes, or repairs.
/// </summary>
public readonly record struct ScopeIdentity
{
    /// <summary>Fail-closed resource bound only; carries no semantics (§16.7.1).</summary>
    public const int MaxLength = 64;

    private readonly string? _value;

    private ScopeIdentity(string value) => _value = value;

    /// <summary>The exact identity string, e.g. <c>us.ssn</c>. Null for <c>default</c>.</summary>
    public string Value => _value!;

    /// <summary>False only for <c>default(ScopeIdentity)</c>.</summary>
    public bool HasValue => _value is not null;

    // §5.1 grammar, reused by §16.7.1: ^[a-z][a-z0-9]*(_[a-z0-9]+)*\.[a-z][a-z0-9]*(_[a-z0-9]+)*$
    // plus length <= MaxLength. Hand-written scanner, ASCII by construction, so no culture or
    // RegexOptions can affect it.
    private static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        if (value.Length > MaxLength) return false;

        int dot = -1;
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '.') continue;
            if (dot >= 0) return false;   // exactly one '.'
            dot = i;
        }
        if (dot < 0) return false;

        return IsSegment(value.AsSpan(0, dot)) && IsSegment(value.AsSpan(dot + 1));
    }

    // segment := [a-z][a-z0-9]*('_'[a-z0-9]+)*
    private static bool IsSegment(ReadOnlySpan<char> s)
    {
        if (s.Length == 0) return false;
        if (s[0] < 'a' || s[0] > 'z') return false;

        bool afterUnderscore = false;
        for (int i = 1; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '_')
            {
                if (afterUnderscore) return false;   // no empty group, no "__"
                afterUnderscore = true;
                continue;
            }
            bool lower = c >= 'a' && c <= 'z';
            bool digit = c >= '0' && c <= '9';
            if (!lower && !digit) return false;
            afterUnderscore = false;
        }
        return !afterUnderscore;   // must not end on '_'
    }

    /// <summary>Never throws, never guesses. On failure <c>id</c> is <c>default</c>.</summary>
    public static bool TryCreate(string? value, out ScopeIdentity id)
    {
        if (!IsValid(value)) { id = default; return false; }
        id = new ScopeIdentity(value!);
        return true;
    }

    /// <summary>Identity only -- never PII.</summary>
    public override string ToString() => _value ?? string.Empty;
}
