namespace Privon.Detection;

/// <summary>
/// D3B0-A A.9 §7.3 -- the representable carrier for normative semantic metadata OUTSIDE the
/// eleven recognized CandidateSemanticIdentity fields, which is what makes R0.5 check 1
/// implementable and testable.
///
/// Values are <c>object?</c> and are NEVER interpreted by any resolver stage;
/// <see cref="TryGetOpaqueValue"/> is never called by R0, R0.5, or R1-R6.
/// </summary>
public sealed class NormativeFieldBag
{
    /// <summary>
    /// §7.3 reserved names -- twelve literals. Eleven are the recognized
    /// CandidateSemanticIdentity field names of §8; <c>Span</c> is reserved additionally and
    /// defensively, because §8's span is carried as two fields and an unreserved <c>Span</c> key
    /// would read as a shadow of it.
    /// </summary>
    private static readonly string[] ReservedNames =
    [
        "DetectorId", "SemanticType", "SpanStart", "SpanLength", "Span", "RiskLevel",
        "ResolutionEvidence", "DetectionConfidence", "ResolutionRank", "CanonicalValue",
        "AliasIdentity", "OwnerClaim",
    ];

    private readonly Dictionary<string, object?> _fields;
    private readonly NormativeFieldName[] _names;

    private NormativeFieldBag(Dictionary<string, object?> fields, NormativeFieldName[] names)
    {
        _fields = fields;
        _names = names;
    }

    /// <summary>The only way other than <see cref="Create"/> to obtain a bag.</summary>
    public static NormativeFieldBag Empty { get; } = new([], []);

    /// <summary>
    /// THE single public construction model -- no TryCreate, no constructor, no builder.
    ///
    /// Accepts raw <c>string?</c> keys so an invalid name is a REPORTABLE construction failure
    /// rather than something the caller must pre-validate. Three violation classes are evaluated
    /// in the frozen precedence InvalidFieldName -> DuplicateFieldName ->
    /// RecognizedNameCollision, EACH OVER THE ENTIRE INPUT LIST before the next begins -- the
    /// same discipline as R0.5, so enumeration order cannot affect the result.
    /// </summary>
    public static NormativeFieldBag Create(IReadOnlyList<KeyValuePair<string?, object?>> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        // Class 1 -- invalid name, over the whole list.
        string? offender = null;
        bool found = false;
        for (int i = 0; i < fields.Count; i++)
        {
            string? key = fields[i].Key;
            if (NormativeFieldName.IsValidName(key)) continue;
            if (!found || IsSmallerKey(key, offender)) { offender = key; found = true; }
        }
        if (found)
            throw new NormativeFieldBagConstructionException(
                NormativeFieldBagConstructionException.InvalidFieldName, offender);

        // Class 2 -- duplicate valid name, over the whole list.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicated = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < fields.Count; i++)
        {
            string key = fields[i].Key!;
            if (!seen.Add(key)) duplicated.Add(key);
        }
        if (duplicated.Count > 0)
            throw new NormativeFieldBagConstructionException(
                NormativeFieldBagConstructionException.DuplicateFieldName, SmallestOf(duplicated));

        // Class 3 -- reserved-name collision, over the whole list.
        var collisions = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < fields.Count; i++)
        {
            string key = fields[i].Key!;
            if (Array.IndexOf(ReservedNames, key) >= 0) collisions.Add(key);
        }
        if (collisions.Count > 0)
            throw new NormativeFieldBagConstructionException(
                NormativeFieldBagConstructionException.RecognizedNameCollision, SmallestOf(collisions));

        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (int i = 0; i < fields.Count; i++) map[fields[i].Key!] = fields[i].Value;

        // Names are materialised at construction and sorted by UTF-8 byte lexicographic order,
        // so enumeration order never depends on insertion order.
        var names = new List<string>(map.Keys);
        names.Sort(Utf8ByteOrder.Compare);

        var typed = new NormativeFieldName[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            NormativeFieldName.TryCreate(names[i], out var n);   // already validated above
            typed[i] = n;
        }
        return new NormativeFieldBag(map, typed);
    }

    // §7.3 canonical selector: null first, then UTF-8 byte lexicographic order of the key.
    private static bool IsSmallerKey(string? candidate, string? current)
    {
        if (candidate is null) return current is not null;
        if (current is null) return false;
        return Utf8ByteOrder.Compare(candidate, current) < 0;
    }

    private static string SmallestOf(HashSet<string> keys)
    {
        string? best = null;
        foreach (string k in keys)
            if (best is null || Utf8ByteOrder.Compare(k, best) < 0) best = k;
        return best!;
    }

    /// <summary>Sorted; enumerable WITHOUT touching values. Defensively copied.</summary>
    public IReadOnlyList<NormativeFieldName> Names => _names;

    public int Count => _names.Length;

    /// <summary>NEVER called by R0.5 or any resolver stage. Values stay opaque.</summary>
    public bool TryGetOpaqueValue(NormativeFieldName name, out object? value)
    {
        if (!name.HasValue) { value = null; return false; }
        return _fields.TryGetValue(name.Value, out value);
    }
}
