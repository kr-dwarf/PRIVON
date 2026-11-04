namespace Privon.Detection;

/// <summary>
/// D3B0-A A.9 §5.1 -- a detector's stable contract identity.
///
/// DETECTOR_IDENTITY_IS_EXPLICIT: never derived from a class name, <c>nameof</c>,
/// <c>GetType().Name</c>, <see cref="IDetector.Name"/>, a registration index, source order, a
/// list position, or reflection. Equality is ordinal and case-sensitive. Not persisted by
/// D3B0; its lexical order is used only in CCSK field 1 (§12.1) and R6_SUPPRESSED_ORDER key 4
/// (§14.2) -- never as an arbitration key.
/// </summary>
public readonly record struct DetectorId
{
    private readonly string? _value;

    private DetectorId(string value) => _value = value;

    /// <summary>The stable identity string, e.g. <c>global.email</c>. Null for <c>default</c>.</summary>
    public string Value => _value!;

    /// <summary>True when this is not <c>default(DetectorId)</c>.</summary>
    public bool HasValue => _value is not null;

    // §5.1 grammar: ^[a-z][a-z0-9]*(_[a-z0-9]+)*\.[a-z][a-z0-9]*(_[a-z0-9]+)*$
    // Hand-written scanner rather than a Regex so the accepted grammar cannot drift behind a
    // pattern string and no culture or RegexOptions can affect it. ASCII by construction.
    private static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;

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
    public static bool TryCreate(string? value, out DetectorId id)
    {
        if (!IsValid(value)) { id = default; return false; }
        id = new DetectorId(value!);
        return true;
    }

    /// <summary>Throws <see cref="ArgumentException"/> for any value outside the §5.1 grammar.</summary>
    public static DetectorId Create(string value)
    {
        if (!TryCreate(value, out var id))
            throw new ArgumentException($"Invalid DetectorId: '{value}'.", nameof(value));
        return id;
    }

    /// <summary>ID only -- never PII.</summary>
    public override string ToString() => _value ?? string.Empty;
}
