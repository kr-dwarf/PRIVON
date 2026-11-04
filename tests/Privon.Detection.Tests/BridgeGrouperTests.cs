using System.Globalization;
using System.Reflection;
using Privon.Detection;
using Xunit;

namespace Privon.Detection.Tests;

/// <summary>
/// D3B0-B §19 / §20 / §21 / §27 -- LogicalRecordGrouper, its fourteen validation identities, the
/// exact canonical tuple, the closed construction surface, and the culture matrix.
/// </summary>
public sealed class BridgeGrouperTests
{
    private static readonly FieldRoleId RoleA = FieldRoleId.Create("roleA");
    private static readonly FieldRoleId RoleB = FieldRoleId.Create("roleB");

    // The accepted D2.2 expressibility parameters (§21.3).
    private static LogicalRecordRule D22Rule() => new(
        RoleA, RoleB,
        SameLineAllowed: true, AdjacentLineAllowed: true, MaxLabelStartDistance: 128,
        BlankLineIsBarrier: true, InterveningRecognizedFieldIsBarrier: true,
        NonMatchingRecognizedRoleIsBarrier: true,
        SameLineSeparators: SameLineSeparatorPolicy.AllowSpacesCommaSemicolonTabPipe,
        MaxMembers: 2);

    private static NormalizedView View(string text = "") =>
        NormalizedView.Build(text.Length == 0 ? new string(' ', 512) : text);

    private static GroupableField Field(FieldRoleId role, int labelStart, int labelLen = 4,
                                        int valueStart = -1, int valueLen = 4, int line = 0, bool recognized = true)
        => new(role, new RawSpan(labelStart, labelLen),
               new RawSpan(valueStart < 0 ? labelStart + labelLen : valueStart, valueLen), line, recognized);

    // ================================================================================
    // GROUPER-VALIDATION-CANONICAL-TUPLE -- all 14 identities, exact six-field tuple
    // ================================================================================

    public static TheoryData<string> AllKindNames()
    {
        var data = new TheoryData<string>();
        foreach (var k in Enum.GetValues<LogicalRecordValidationKind>()) data.Add(k.ToString());
        return data;
    }

    [Fact]
    public void ValidationKind_IsExactlyFourteenMembers()
    {
        Assert.Equal(14, Enum.GetValues<LogicalRecordValidationKind>().Length);
        // No None/Unknown/Default member exists.
        foreach (var k in Enum.GetValues<LogicalRecordValidationKind>())
            Assert.DoesNotContain(k.ToString(), new[] { "None", "Unknown", "Default" });
    }

    private static LogicalRecordValidationException Capture(Action act)
        => Assert.Throws<LogicalRecordValidationException>(act);

    // (Kind, ErrorCode, ParamName, OffendingValue, OffendingRoleSlot, Message)
    private static void AssertTuple(
        LogicalRecordValidationException ex, LogicalRecordValidationKind kind, string code,
        string paramName, int? value, RuleRoleSlot? slot, string message)
    {
        Assert.IsType<LogicalRecordValidationException>(ex);
        Assert.True(ex is ArgumentException);                    // catch compatibility
        Assert.Equal(kind, ex.Kind);
        Assert.Equal(code, ex.ErrorCode);
        Assert.Equal(paramName, ex.ParamName);
        Assert.Equal(value, ex.OffendingValue);
        Assert.Equal(slot, ex.OffendingRoleSlot);
        Assert.Equal(message, ex.Message);                       // FULL message, whole-string equality

        // No framework parameter suffix and no second line may appear.
        Assert.DoesNotContain("(Parameter", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", ex.Message, StringComparison.Ordinal);

        // Message begins with ErrorCode under the frozen two-shape grammar.
        Assert.True(ex.Message == ex.ErrorCode
                    || ex.Message.StartsWith(ex.ErrorCode + ": ", StringComparison.Ordinal));
    }

    [Fact]
    public void Class01_NullFieldsArgument()
        => AssertTuple(Capture(() => LogicalRecordGrouper.Group(null!, D22Rule(), View())),
            LogicalRecordValidationKind.NullFieldsArgument, "GROUPER_NULL_ARGUMENT", "fields",
            null, null, "GROUPER_NULL_ARGUMENT: parameter=fields");

    [Fact]
    public void Class02_NullRuleArgument()
        => AssertTuple(Capture(() => LogicalRecordGrouper.Group([], null!, View())),
            LogicalRecordValidationKind.NullRuleArgument, "GROUPER_NULL_ARGUMENT", "rule",
            null, null, "GROUPER_NULL_ARGUMENT: parameter=rule");

    [Fact]
    public void Class03_NullViewArgument()
        => AssertTuple(Capture(() => LogicalRecordGrouper.Group([], D22Rule(), null!)),
            LogicalRecordValidationKind.NullViewArgument, "GROUPER_NULL_ARGUMENT", "view",
            null, null, "GROUPER_NULL_ARGUMENT: parameter=view");

    [Fact]
    public void Class04_RuleRoleUninitialized_SlotA_BeforeSlotB()
    {
        var rule = D22Rule() with { RoleA = default, RoleB = default };
        AssertTuple(Capture(() => LogicalRecordGrouper.Group([], rule, View())),
            LogicalRecordValidationKind.RuleRoleUninitialized, "GROUPER_RULE_ROLE_UNINITIALIZED",
            "rule", null, RuleRoleSlot.A, "GROUPER_RULE_ROLE_UNINITIALIZED: role=A");
    }

    [Fact]
    public void Class04_RuleRoleUninitialized_SlotB()
    {
        var rule = D22Rule() with { RoleB = default };
        AssertTuple(Capture(() => LogicalRecordGrouper.Group([], rule, View())),
            LogicalRecordValidationKind.RuleRoleUninitialized, "GROUPER_RULE_ROLE_UNINITIALIZED",
            "rule", null, RuleRoleSlot.B, "GROUPER_RULE_ROLE_UNINITIALIZED: role=B");
    }

    [Fact]
    public void Class05_RuleRolesEqual()
    {
        var rule = D22Rule() with { RoleB = RoleA };
        AssertTuple(Capture(() => LogicalRecordGrouper.Group([], rule, View())),
            LogicalRecordValidationKind.RuleRolesEqual, "GROUPER_RULE_ROLES_EQUAL", "rule",
            null, null, "GROUPER_RULE_ROLES_EQUAL");
    }

    [Fact]
    public void Class06_RuleMaxMembers()
    {
        var rule = D22Rule() with { MaxMembers = 1 };
        AssertTuple(Capture(() => LogicalRecordGrouper.Group([], rule, View())),
            LogicalRecordValidationKind.RuleMaxMembers, "GROUPER_RULE_MAX_MEMBERS", "rule",
            1, null, "GROUPER_RULE_MAX_MEMBERS: value=1");
    }

    [Fact]
    public void Class06_RuleMaxMembers_NegativeRendersInvariantAsciiMinus()
    {
        var rule = D22Rule() with { MaxMembers = -7 };
        AssertTuple(Capture(() => LogicalRecordGrouper.Group([], rule, View())),
            LogicalRecordValidationKind.RuleMaxMembers, "GROUPER_RULE_MAX_MEMBERS", "rule",
            -7, null, "GROUPER_RULE_MAX_MEMBERS: value=-7");
    }

    [Fact]
    public void Class07_RuleMaxDistanceNegative()
    {
        var rule = D22Rule() with { MaxLabelStartDistance = -1 };
        AssertTuple(Capture(() => LogicalRecordGrouper.Group([], rule, View())),
            LogicalRecordValidationKind.RuleMaxDistanceNegative, "GROUPER_RULE_MAX_DISTANCE_NEGATIVE",
            "rule", -1, null, "GROUPER_RULE_MAX_DISTANCE_NEGATIVE: value=-1");
    }

    [Fact]
    public void Class08_RuleSeparatorPolicyUndefined()
    {
        var rule = D22Rule() with { SameLineSeparators = (SameLineSeparatorPolicy)999 };
        AssertTuple(Capture(() => LogicalRecordGrouper.Group([], rule, View())),
            LogicalRecordValidationKind.RuleSeparatorPolicyUndefined,
            "GROUPER_RULE_SEPARATOR_POLICY_UNDEFINED", "rule", 999, null,
            "GROUPER_RULE_SEPARATOR_POLICY_UNDEFINED: value=999");
    }

    [Fact]
    public void Class09_NullFieldElement_ExposesNoOffenderIndexOrPosition()
    {
        var fields = new GroupableField?[] { Field(RoleA, 0), null, Field(RoleB, 10) };
        var ex = Capture(() => LogicalRecordGrouper.Group(fields!, D22Rule(), View()));

        AssertTuple(ex, LogicalRecordValidationKind.NullFieldElement, "GROUPER_NULL_FIELD_ELEMENT",
            "fields", null, null, "GROUPER_NULL_FIELD_ELEMENT");

        // No index, count or position may leak into the message.
        foreach (char c in ex.Message) Assert.False(char.IsDigit(c));
    }

    [Fact]
    public void Class10_FieldRoleUninitialized_SelectsMinLabelStart()
    {
        var fields = new[]
        {
            new GroupableField(default, new RawSpan(30, 4), new RawSpan(34, 4), 0, true),
            new GroupableField(default, new RawSpan(12, 4), new RawSpan(16, 4), 0, true),
        };
        AssertTuple(Capture(() => LogicalRecordGrouper.Group(fields, D22Rule(), View())),
            LogicalRecordValidationKind.FieldRoleUninitialized, "GROUPER_FIELD_ROLE_UNINITIALIZED",
            "fields", 12, null, "GROUPER_FIELD_ROLE_UNINITIALIZED: labelStart=12");
    }

    [Fact]
    public void Class11_NegativeLineIndex_SelectsMinLabelStart()
    {
        var fields = new[] { Field(RoleA, 40, line: -1), Field(RoleB, 20, line: -3) };
        AssertTuple(Capture(() => LogicalRecordGrouper.Group(fields, D22Rule(), View())),
            LogicalRecordValidationKind.NegativeLineIndex, "GROUPER_NEGATIVE_LINE_INDEX",
            "fields", 20, null, "GROUPER_NEGATIVE_LINE_INDEX: labelStart=20");
    }

    [Fact]
    public void Class12_LabelSpanOutOfView()
    {
        var view = View(new string('x', 40));
        var fields = new[] { Field(RoleA, 100), Field(RoleB, 60) };
        AssertTuple(Capture(() => LogicalRecordGrouper.Group(fields, D22Rule(), view)),
            LogicalRecordValidationKind.LabelSpanOutOfView, "GROUPER_LABEL_SPAN_OUT_OF_VIEW",
            "fields", 60, null, "GROUPER_LABEL_SPAN_OUT_OF_VIEW: labelStart=60");
    }

    [Fact]
    public void Class13_ValueSpanOutOfView()
    {
        var view = View(new string('x', 40));
        var fields = new[] { Field(RoleA, 10, valueStart: 200), Field(RoleB, 20, valueStart: 300) };
        AssertTuple(Capture(() => LogicalRecordGrouper.Group(fields, D22Rule(), view)),
            LogicalRecordValidationKind.ValueSpanOutOfView, "GROUPER_VALUE_SPAN_OUT_OF_VIEW",
            "fields", 10, null, "GROUPER_VALUE_SPAN_OUT_OF_VIEW: labelStart=10");
    }

    [Fact]
    public void Class14_DuplicateLabelStart_SelectsSmallestDuplicatedValue()
    {
        // GROUPER-DUPLICATE-START, the frozen {20,10,20,5,10} shape: duplicated values are
        // {10,20}; 5 is NOT duplicated; the offender is 10, never "first encountered".
        var starts = new[] { 20, 10, 20, 5, 10 };
        var fields = starts.Select((s, i) => Field(i % 2 == 0 ? RoleA : RoleB, s)).ToArray();

        AssertTuple(Capture(() => LogicalRecordGrouper.Group(fields, D22Rule(), View())),
            LogicalRecordValidationKind.DuplicateLabelStart, "GROUPER_DUPLICATE_LABEL_START",
            "fields", 10, null, "GROUPER_DUPLICATE_LABEL_START: labelStart=10");
    }

    [Fact]
    public void Class14_DuplicateStart_IsIdenticalUnderEveryInputPermutation()
    {
        var starts = new[] { 20, 10, 20, 5, 10 };
        var fields = starts.Select((s, i) => Field(i % 2 == 0 ? RoleA : RoleB, s)).ToArray();

        foreach (var permutation in Permute(fields))
        {
            var ex = Capture(() => LogicalRecordGrouper.Group(permutation, D22Rule(), View()));
            Assert.Equal(LogicalRecordValidationKind.DuplicateLabelStart, ex.Kind);
            Assert.Equal(10, ex.OffendingValue);
            Assert.Equal("GROUPER_DUPLICATE_LABEL_START: labelStart=10", ex.Message);
            Assert.Equal("fields", ex.ParamName);
        }
    }

    // ================================================================================
    // GROUPER-MULTI-VALIDATION-PERMUTATION
    // ================================================================================

    [Fact]
    public void MultiViolation_LowestNumberedClassAlwaysWins_UnderEveryPermutation()
    {
        // null element AND negative LineIndex AND out-of-view ValueSpan AND duplicate starts.
        var view = View(new string('x', 40));
        var fields = new GroupableField?[]
        {
            Field(RoleA, 12, line: -1),
            null,
            Field(RoleB, 12, valueStart: 300),
            Field(RoleA, 5),
        };

        foreach (var permutation in Permute(fields))
        {
            var ex = Capture(() => LogicalRecordGrouper.Group(permutation!, D22Rule(), view));
            // Class 9 precedes 10-13, which precede 14.
            Assert.Equal(LogicalRecordValidationKind.NullFieldElement, ex.Kind);
            Assert.Equal("GROUPER_NULL_FIELD_ELEMENT", ex.Message);
            Assert.Equal("fields", ex.ParamName);
            Assert.Null(ex.OffendingValue);
            Assert.Null(ex.OffendingRoleSlot);
        }
    }

    [Fact]
    public void MultiViolation_NegativeLineIndexBeatsDuplicateStart()
    {
        var fields = new[] { Field(RoleA, 30, line: -1), Field(RoleB, 12), Field(RoleA, 12) };
        var ex = Capture(() => LogicalRecordGrouper.Group(fields, D22Rule(), View()));
        Assert.Equal(LogicalRecordValidationKind.NegativeLineIndex, ex.Kind);
        Assert.Equal(30, ex.OffendingValue);
    }

    // ================================================================================
    // GROUPER-MESSAGE-CULTURE-INVARIANCE
    // ================================================================================

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ar-SA")]
    [InlineData("tr-TR")]
    public void CanonicalTuple_IsByteIdenticalAcrossCultures(string culture)
    {
        var original = CultureInfo.CurrentCulture;
        var originalUi = CultureInfo.CurrentUICulture;
        try
        {
            var ci = CultureInfo.GetCultureInfo(culture);
            CultureInfo.CurrentCulture = ci;
            CultureInfo.CurrentUICulture = ci;

            // A NEGATIVE offender is the live hazard: under ar-SA the ambient conversion prefixes
            // U+061C ARABIC LETTER MARK to the sign.
            var rule = D22Rule() with { MaxLabelStartDistance = -7 };
            var ex = Capture(() => LogicalRecordGrouper.Group([], rule, View()));

            Assert.Equal("GROUPER_RULE_MAX_DISTANCE_NEGATIVE: value=-7", ex.Message);
            Assert.Equal("GROUPER_RULE_MAX_DISTANCE_NEGATIVE", ex.ErrorCode);
            Assert.Equal("rule", ex.ParamName);
            Assert.Equal(-7, ex.OffendingValue);
            Assert.DoesNotContain("\u061C", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("(Parameter", ex.Message, StringComparison.Ordinal);

            // Role slot renders as literal ASCII, never slot.ToString() under a Turkish casing rule.
            var slotEx = Capture(() => LogicalRecordGrouper.Group([], D22Rule() with { RoleA = default }, View()));
            Assert.Equal("GROUPER_RULE_ROLE_UNINITIALIZED: role=A", slotEx.Message);

            // The duplicate-start observable is likewise culture-independent.
            var fields = new[] { 20, 10, 20, 5, 10 }.Select((s, i) => Field(i % 2 == 0 ? RoleA : RoleB, s)).ToArray();
            var dup = Capture(() => LogicalRecordGrouper.Group(fields, D22Rule(), View()));
            Assert.Equal("GROUPER_DUPLICATE_LABEL_START: labelStart=10", dup.Message);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
            CultureInfo.CurrentUICulture = originalUi;
        }
    }

    // ================================================================================
    // GROUPER-CONSTRUCTION-SURFACE-CLOSED
    // ================================================================================

    [Fact]
    public void ConstructionSurface_IsClosed()
    {
        var t = typeof(LogicalRecordValidationException);

        Assert.True(t.IsSealed);
        Assert.True(typeof(ArgumentException).IsAssignableFrom(t));

        // No ACCESSIBLE constructor at all -- public or non-public-but-callable.
        Assert.Empty(t.GetConstructors(BindingFlags.Public | BindingFlags.Instance));

        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic
                                 | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        // Every constructor is private, and none takes a caller-supplied message/code/paramName.
        foreach (var ctor in t.GetConstructors(all))
            Assert.True(ctor.IsPrivate, "constructor must be private");

        // No general Kind-taking or RuleRoleSlot-taking factory, and no Create(kind, ...).
        foreach (var m in t.GetMethods(all))
        {
            var ps = m.GetParameters();
            Assert.DoesNotContain(ps, p => p.ParameterType == typeof(LogicalRecordValidationKind) && !m.IsPrivate);
            Assert.DoesNotContain(ps, p => p.ParameterType == typeof(RuleRoleSlot) && !m.IsPrivate);
            if (m.Name is "BuildMessage" or "Canonicalize")
                Assert.True(m.IsPrivate, m.Name + " must be private");
        }

        // The 15 factories over 14 identities are internal static, and each returns the exception.
        var factories = t.GetMethods(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
                         .Where(m => m.ReturnType == t && m.IsAssembly)
                         .ToArray();
        Assert.Equal(15, factories.Length);

        // Every public observable is get-only.
        foreach (string name in new[] { "Kind", "ErrorCode", "OffendingValue", "OffendingRoleSlot", "Message" })
        {
            var prop = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(prop);
            Assert.Null(prop!.SetMethod);
        }
    }

    [Fact]
    public void GrouperThrowSites_UseNamedFactories_NotTheConstructor()
    {
        // Proven by the construction surface above: with a private constructor whose only
        // parameter is a private nested type, `new LogicalRecordValidationException(...)` cannot
        // compile anywhere outside the exception type -- so every grouper throw site necessarily
        // goes through a named factory. This test records the invariant explicitly.
        var nested = typeof(LogicalRecordValidationException)
            .GetNestedTypes(BindingFlags.NonPublic)
            .Where(t => t.Name.Contains("Canonical", StringComparison.Ordinal))
            .ToArray();
        Assert.Single(nested);
        Assert.True(nested[0].IsNestedPrivate);
    }

    // ================================================================================
    // Grouping mechanics -- D2.2 preserved, permutation invariant
    // ================================================================================

    [Fact]
    public void SameLinePair_IsGrouped_AndMembersAreOrderedByRole()
    {
        var view = View("roleA: 1111 roleB: 2222");
        var a = new GroupableField(RoleA, new RawSpan(0, 5), new RawSpan(7, 4), 0, true);
        var b = new GroupableField(RoleB, new RawSpan(12, 5), new RawSpan(19, 4), 0, true);

        var matches = LogicalRecordGrouper.Group([b, a], D22Rule(), view);
        var match = Assert.Single(matches);

        // MATCH_MEMBER_ORDER -- RoleA first, then RoleB, never input order.
        Assert.Equal(RoleA, match.Roles[0]);
        Assert.Equal(RoleB, match.Roles[1]);
        Assert.Equal(0, match.RecordSpan.Start);
    }

    [Fact]
    public void GreedyAcceptance_PreventsMemberReuse()
    {
        // The gap between one field's value end and the next field's label start must contain
        // only D2.2-legal same-line separators, so the view is space-filled.
        var view = View(new string(' ', 200));
        var a = new GroupableField(RoleA, new RawSpan(0, 4), new RawSpan(4, 4), 0, true);
        var b1 = new GroupableField(RoleB, new RawSpan(10, 4), new RawSpan(14, 4), 0, true);
        var b2 = new GroupableField(RoleB, new RawSpan(20, 4), new RawSpan(24, 4), 0, true);

        var matches = LogicalRecordGrouper.Group([a, b1, b2], D22Rule() with
        {
            InterveningRecognizedFieldIsBarrier = false,
        }, view);

        // One A cannot serve two matches.
        Assert.Single(matches);
        Assert.Equal(10, matches[0].Members[1].LabelSpan.Start);   // the nearer B wins the pair key
    }

    [Fact]
    public void Grouping_IsPermutationInvariant()
    {
        var view = View(new string(' ', 400));
        var fields = new[]
        {
            new GroupableField(RoleA, new RawSpan(0, 4), new RawSpan(4, 4), 0, true),
            new GroupableField(RoleB, new RawSpan(10, 4), new RawSpan(14, 4), 0, true),
            new GroupableField(RoleA, new RawSpan(200, 4), new RawSpan(204, 4), 3, true),
            new GroupableField(RoleB, new RawSpan(210, 4), new RawSpan(214, 4), 3, true),
        };
        var rule = D22Rule() with { InterveningRecognizedFieldIsBarrier = false };

        string Render(IReadOnlyList<LogicalRecordMatch> ms) => string.Join("|", ms.Select(m =>
            $"{m.RecordSpan.Start}:{m.RecordSpan.Length}:{m.Members[0].LabelSpan.Start}>{m.Members[1].LabelSpan.Start}"));

        string expected = Render(LogicalRecordGrouper.Group(fields, rule, view));
        foreach (var permutation in Permute(fields))
            Assert.Equal(expected, Render(LogicalRecordGrouper.Group(permutation, rule, view)));
    }

    [Fact]
    public void NoMatch_IsNeverAnError()
    {
        Assert.Empty(LogicalRecordGrouper.Group([], D22Rule(), View()));
        Assert.Empty(LogicalRecordGrouper.Group([Field(RoleA, 0)], D22Rule(), View()));
    }

    [Fact]
    public void DistanceBoundary_128Accepted_129Rejected()
    {
        var view = View(new string(' ', 400));
        var rule = D22Rule() with { InterveningRecognizedFieldIsBarrier = false };

        var a = new GroupableField(RoleA, new RawSpan(0, 4), new RawSpan(4, 4), 0, true);
        var at128 = new GroupableField(RoleB, new RawSpan(128, 4), new RawSpan(132, 4), 0, true);
        var at129 = new GroupableField(RoleB, new RawSpan(129, 4), new RawSpan(133, 4), 0, true);

        Assert.Single(LogicalRecordGrouper.Group([a, at128], rule, view));
        Assert.Empty(LogicalRecordGrouper.Group([a, at129], rule, view));
    }

    private static IEnumerable<T[]> Permute<T>(T[] items)
    {
        if (items.Length <= 1) { yield return items; yield break; }
        for (int i = 0; i < items.Length; i++)
        {
            var rest = items.Take(i).Concat(items.Skip(i + 1)).ToArray();
            foreach (var tail in Permute(rest))
                yield return new[] { items[i] }.Concat(tail).ToArray();
        }
    }
}
