namespace Privon.Detection;

/// <summary>D3B0-A A.9 §21.3 -- a caller-supplied, opaque field role. The grouper never interprets its text.</summary>
public readonly record struct FieldRoleId
{
    private readonly string? _value;
    private FieldRoleId(string value) => _value = value;

    public string Value => _value!;
    public bool HasValue => _value is not null;

    public static bool TryCreate(string? value, out FieldRoleId id)
    {
        id = default;
        if (string.IsNullOrEmpty(value)) return false;
        id = new FieldRoleId(value);
        return true;
    }

    public static FieldRoleId Create(string value) =>
        TryCreate(value, out var id) ? id : throw new ArgumentException("Invalid FieldRoleId.", nameof(value));

    public override string ToString() => _value ?? string.Empty;
}

/// <summary>
/// D3B0-A A.9 §21.3 -- the grouper's whole input model. It reads ONLY these fields and contains
/// no ABA, routing, account, or any other U.S. or financial semantics; roles are opaque
/// identifiers supplied by the caller.
///
/// INTRINSIC_FIELD_IDENTITY (§21.3.1): a field's identity within one Group call is its
/// <see cref="LabelSpan"/>.Start -- a source-derived intrinsic value invariant under any
/// permutation of the input. GroupableFieldId does NOT exist as a type, field, parameter or key
/// component, and no caller-supplied surrogate id replaces it.
/// </summary>
public sealed record GroupableField(
    FieldRoleId Role, RawSpan LabelSpan, RawSpan ValueSpan, int LineIndex, bool IsRecognized);

/// <summary>D3B0-A A.9 §21.3 -- the caller-supplied pairing rule. D3B0 supplies no role names or label lists.</summary>
public sealed record LogicalRecordRule(
    FieldRoleId RoleA,
    FieldRoleId RoleB,
    bool SameLineAllowed,
    bool AdjacentLineAllowed,
    int MaxLabelStartDistance,
    bool BlankLineIsBarrier,
    bool InterveningRecognizedFieldIsBarrier,
    bool NonMatchingRecognizedRoleIsBarrier,
    SameLineSeparatorPolicy SameLineSeparators,
    int MaxMembers);

/// <summary>
/// D3B0-A A.9 §21.3.3 -- one accepted logical record. Members are ordered BY ROLE (MATCH_MEMBER_ORDER):
/// the RoleA member first, then the RoleB member, with <see cref="Roles"/> parallel to
/// <see cref="Members"/>. NEVER dictionary, list, insertion, or input order.
/// </summary>
public sealed record LogicalRecordMatch(
    IReadOnlyList<GroupableField> Members,
    IReadOnlyList<FieldRoleId> Roles,
    RawSpan RecordSpan,
    int FirstLineIndex,
    int LastLineIndex);

/// <summary>
/// D3B0-A A.9 §21.3 -- the complete generic grouping mechanism. Only detector-specific label and
/// role CONTENTS are deferred to the owning detector unit; D3B0 supplies none.
///
/// GROUPER_PERMUTATION_INVARIANCE (§21.3.5): for the same logical field set, EVERY input
/// permutation produces identical match membership, identical selected pairings, identical member
/// order within each match, and identical result order. This follows structurally -- step 1
/// enumerates unordered pairs of a set, steps 2-7 are per-pair predicates over intrinsic values,
/// step 8 sorts by a total order on intrinsic values, the output order is a total order on
/// intrinsic values, and member order is fixed by role.
/// </summary>
public static class LogicalRecordGrouper
{
    /// <summary>
    /// <paramref name="fields"/> is an IReadOnlyCollection -- deliberately NOT a list -- so no
    /// caller can imply that supply order carries meaning.
    /// </summary>
    public static IReadOnlyList<LogicalRecordMatch> Group(
        IReadOnlyCollection<GroupableField> fields,
        LogicalRecordRule rule,
        NormalizedView view)
    {
        Validate(fields, rule, view);

        var all = fields.ToList();

        // ---- step 1: candidate pair enumeration over UNORDERED pairs of a set ----
        var surviving = new List<(GroupableField A, GroupableField B, int Min, int Max)>();
        for (int i = 0; i < all.Count; i++)
            for (int j = i + 1; j < all.Count; j++)
            {
                var x = all[i];
                var y = all[j];
                if (!x.IsRecognized || !y.IsRecognized) continue;

                // one has RoleA and the other RoleB
                GroupableField a, b;
                if (x.Role == rule.RoleA && y.Role == rule.RoleB) { a = x; b = y; }
                else if (y.Role == rule.RoleA && x.Role == rule.RoleB) { a = y; b = x; }
                else continue;

                if (!Accepts(a, b, rule, all, view.RawText)) continue;

                int lo = Math.Min(a.LabelSpan.Start, b.LabelSpan.Start);
                int hi = Math.Max(a.LabelSpan.Start, b.LabelSpan.Start);
                surviving.Add((a, b, lo, hi));
            }

        // ---- step 8: deterministic selection among surviving pairs ----
        // PairSelectionKey(P) = (min(LabelSpan.Start), max(LabelSpan.Start)), compared
        // lexicographically ascending. TOTAL: LabelSpan.Start is injective over the field set
        // (enforced by validation class 14), so equal keys force the same two fields. Both
        // components are intrinsic and permutation-invariant. No third or fourth component is
        // needed, and none may be added.
        surviving.Sort(static (p, q) =>
        {
            int c = p.Min.CompareTo(q.Min);
            return c != 0 ? c : p.Max.CompareTo(q.Max);
        });

        var used = new HashSet<int>();          // by intrinsic LabelSpan.Start
        var matches = new List<LogicalRecordMatch>();
        foreach (var (a, b, _, _) in surviving)
        {
            // Greedy acceptance: accept iff NEITHER member already belongs to an accepted match.
            // Same greedy discipline as R4; prevents member reuse.
            if (used.Contains(a.LabelSpan.Start) || used.Contains(b.LabelSpan.Start)) continue;
            used.Add(a.LabelSpan.Start);
            used.Add(b.LabelSpan.Start);

            int recordStart = Math.Min(a.LabelSpan.Start, b.LabelSpan.Start);
            int recordEnd = Math.Max(a.ValueSpan.Start + a.ValueSpan.Length, b.ValueSpan.Start + b.ValueSpan.Length);

            matches.Add(new LogicalRecordMatch(
                [a, b],                                   // MATCH_MEMBER_ORDER: RoleA then RoleB
                [a.Role, b.Role],
                new RawSpan(recordStart, recordEnd - recordStart),
                Math.Min(a.LineIndex, b.LineIndex),
                Math.Max(a.LineIndex, b.LineIndex)));
        }

        // ---- MATCH_OUTPUT_ORDER (§21.3.3) ----
        // RecordSpan.Start asc -> RecordSpan.Length asc -> Members[0].LabelSpan.Start asc ->
        // Members[1].LabelSpan.Start asc. TOTAL: accepted matches share no member and
        // LabelSpan.Start is injective, so key 3 alone separates every distinct pair of matches;
        // key 4 is a declared-UNREACHABLE component kept so the order stays total if a future
        // audited rule raises MaxMembers.
        matches.Sort(static (m, n) =>
        {
            int c = m.RecordSpan.Start.CompareTo(n.RecordSpan.Start);
            if (c != 0) return c;
            c = m.RecordSpan.Length.CompareTo(n.RecordSpan.Length);
            if (c != 0) return c;
            c = m.Members[0].LabelSpan.Start.CompareTo(n.Members[0].LabelSpan.Start);
            if (c != 0) return c;
            return m.Members[1].LabelSpan.Start.CompareTo(n.Members[1].LabelSpan.Start);
        });

        // No match is NEVER an error and never a candidate.
        return matches;
    }

    // ================================================================================
    // §21.3.4 GROUPER_VALIDATION_PRECEDENCE -- fourteen classes in four tiers, evaluated
    // CLASS-MAJOR: each class is evaluated over the ENTIRE input before the next class begins,
    // so the FIRST class with any violation wins regardless of which violation a naive single
    // pass would have met first.
    //
    // GROUPER_OFFENDER_IS_A_VALUE_NOT_A_FIELD: every selector selects a numeric (or single-letter)
    // value, never a field object, reference, or collection index -- so two violating elements
    // sharing a LabelSpan.Start still yield ONE unambiguous observable.
    //
    // GROUPER_VALIDATION_IS_PERMUTATION_INVARIANT: no class, class selection, or offender
    // selection reads collection position, enumeration order, insertion order, or a surrogate id.
    // ================================================================================
    private static void Validate(IReadOnlyCollection<GroupableField> fields, LogicalRecordRule rule, NormalizedView view)
    {
        // tier 1 -- argument existence (classes 1-3), in this frozen order
        if (fields is null) throw LogicalRecordValidationException.NullFieldsArgument();
        if (rule is null) throw LogicalRecordValidationException.NullRuleArgument();
        if (view is null) throw LogicalRecordValidationException.NullViewArgument();

        // tier 2 -- rule well-formedness (classes 4-8)
        if (!rule.RoleA.HasValue) throw LogicalRecordValidationException.RuleRoleUninitializedSlotA();
        if (!rule.RoleB.HasValue) throw LogicalRecordValidationException.RuleRoleUninitializedSlotB();
        if (rule.RoleA == rule.RoleB) throw LogicalRecordValidationException.RuleRolesEqual();
        if (rule.MaxMembers < 2) throw LogicalRecordValidationException.RuleMaxMembers(rule.MaxMembers);
        if (rule.MaxLabelStartDistance < 0)
            throw LogicalRecordValidationException.RuleMaxDistanceNegative(rule.MaxLabelStartDistance);
        if (rule.SameLineSeparators is not (SameLineSeparatorPolicy.Disallowed
                                            or SameLineSeparatorPolicy.AllowSpacesCommaSemicolonTabPipe))
            throw LogicalRecordValidationException.RuleSeparatorPolicyUndefined((int)rule.SameLineSeparators);

        // tier 3 -- element well-formedness (classes 9-13), each over the WHOLE set
        foreach (var f in fields)
            if (f is null) throw LogicalRecordValidationException.NullFieldElement();   // no index/count/position

        int viewLength = view.RawText.Length;

        if (TryMinViolating(fields, f => !f.Role.HasValue, out int offender))
            throw LogicalRecordValidationException.FieldRoleUninitialized(offender);

        if (TryMinViolating(fields, f => f.LineIndex < 0, out offender))
            throw LogicalRecordValidationException.NegativeLineIndex(offender);

        if (TryMinViolating(fields, f => !InView(f.LabelSpan, viewLength), out offender))
            throw LogicalRecordValidationException.LabelSpanOutOfView(offender);

        if (TryMinViolating(fields, f => !InView(f.ValueSpan, viewLength), out offender))
            throw LogicalRecordValidationException.ValueSpanOutOfView(offender);

        // tier 4 -- set uniqueness (class 14): the SMALLEST DUPLICATED LabelSpan.Start value.
        // Never "first duplicate encountered": for starts {20,10,20,5,10} the duplicated values
        // are {10,20} and 5 is not duplicated, so the offender is 10 under every permutation.
        var seen = new HashSet<int>();
        var duplicated = new HashSet<int>();
        foreach (var f in fields)
            if (!seen.Add(f.LabelSpan.Start)) duplicated.Add(f.LabelSpan.Start);

        if (duplicated.Count > 0)
        {
            int min = int.MaxValue;
            foreach (int v in duplicated) if (v < min) min = v;
            throw LogicalRecordValidationException.DuplicateLabelStart(min);
        }
    }

    // Selector for classes 10-13: the MINIMUM signed LabelSpan.Start over the violating set.
    private static bool TryMinViolating(
        IReadOnlyCollection<GroupableField> fields, Func<GroupableField, bool> violates, out int offender)
    {
        bool found = false;
        offender = 0;
        foreach (var f in fields)
        {
            if (!violates(f)) continue;
            if (!found || f.LabelSpan.Start < offender) { offender = f.LabelSpan.Start; found = true; }
        }
        return found;
    }

    private static bool InView(RawSpan span, int viewLength) =>
        span.Start >= 0 && span.Length >= 0 && span.Length <= viewLength - span.Start;

    // ================================================================================
    // §21.3 steps 2-7 -- per-pair predicates over intrinsic values only. No step reads
    // collection position, enumeration order, or any surrogate id.
    // ================================================================================
    private static bool Accepts(
        GroupableField a, GroupableField b, LogicalRecordRule rule,
        IReadOnlyList<GroupableField> all, string raw)
    {
        // step 2 -- locality
        bool sameLine = a.LineIndex == b.LineIndex;
        int lineDelta = Math.Abs(a.LineIndex - b.LineIndex);
        if (sameLine)
        {
            if (!rule.SameLineAllowed) return false;
        }
        else if (lineDelta == 1)
        {
            if (!rule.AdjacentLineAllowed) return false;
        }
        else return false;

        int lo = Math.Min(a.LabelSpan.Start, b.LabelSpan.Start);
        int hi = Math.Max(a.LabelSpan.Start, b.LabelSpan.Start);

        // step 3 -- blank-line barrier. For adjacent lines there is no line strictly between, so
        // this only constrains |delta| > 1, which step 2 already rejects; retained so the rule
        // object stays complete if a future audited rule widens AdjacentLineAllowed.
        if (rule.BlankLineIsBarrier && lineDelta > 1) return false;

        // step 4 -- interruption barrier
        if (rule.InterveningRecognizedFieldIsBarrier)
        {
            foreach (var third in all)
            {
                if (!third.IsRecognized) continue;
                if (third.LabelSpan.Start == a.LabelSpan.Start || third.LabelSpan.Start == b.LabelSpan.Start) continue;
                if (third.LabelSpan.Start <= lo || third.LabelSpan.Start >= hi) continue;

                if (rule.NonMatchingRecognizedRoleIsBarrier) return false;
                if (third.Role == rule.RoleA || third.Role == rule.RoleB) return false;
            }
        }

        // step 5 -- distance. With D2.2's 128, a label-start distance of 128 is accepted and 129
        // is rejected.
        if (hi - lo > rule.MaxLabelStartDistance) return false;

        // step 6 -- same-line separator policy
        if (sameLine)
        {
            if (rule.SameLineSeparators == SameLineSeparatorPolicy.Disallowed) return false;

            var earlier = a.LabelSpan.Start <= b.LabelSpan.Start ? a : b;
            var later = ReferenceEquals(earlier, a) ? b : a;
            int from = earlier.ValueSpan.Start + earlier.ValueSpan.Length;
            int to = later.LabelSpan.Start;

            if (from < 0 || to > raw.Length) return false;
            for (int i = from; i < to; i++)
            {
                char c = raw[i];
                if (c != ' ' && c != ',' && c != ';' && c != '\t' && c != '|') return false;
            }
        }

        // step 7 -- member cap. A match contains exactly the fields of the accepted pair;
        // MaxMembers bounds member count and is 2 under D2.2 (MaxMembers < 2 is a configuration
        // error, already rejected by validation class 6).
        return true;
    }
}
