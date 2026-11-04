using System.Globalization;

namespace Privon.Detection;

/// <summary>
/// D3B0-A A.9 §21.3.4b -- the single PRIVON-owned LogicalRecordGrouper input-validation failure.
///
/// PUBLIC_MESSAGE_IS_PRIVON_OWNED (§21.3.4a): the full public <see cref="Message"/> is
/// constructed entirely from PRIVON-owned invariant data and returned verbatim. It is never
/// derived from, appended to, parsed out of, or reconstructed from any framework-generated text.
/// The <see cref="Message"/> override is MANDATORY, not stylistic: measured on .NET 10.0.11 the
/// inherited ArgumentException getter returns <c>message + " (Parameter 'x')"</c> ON THE SAME
/// LINE, so the withdrawn "first line is normative" rule separated nothing.
///
/// NO_EXTERNAL_EXCEPTION_CONSTRUCTION (§21.3.4b): the constructor is PRIVATE and its only
/// parameter type is the private nested <see cref="Canonical"/>, which no other code can NAME --
/// so the constructor is not merely inaccessible but unforgeable. The only construction paths are
/// the fifteen named internal factories below, each fixing its own Kind as a LITERAL.
///
/// UNKNOWN_KIND_IS_UNREPRESENTABLE / INVALID_ROLE_SLOT_IS_UNREPRESENTABLE: no accessible surface
/// accepts a <see cref="LogicalRecordValidationKind"/> or a <see cref="RuleRoleSlot"/>, so
/// <c>(LogicalRecordValidationKind)999</c> and <c>(RuleRoleSlot)999</c> cannot cross the
/// construction boundary at all. There is therefore NO second failure contract for an invalid
/// kind, and the private switches' default arms are UNREACHABLE by construction.
///
/// GROUPER_VALIDATION_IS_OUTSIDE_THE_RESOLVER_UNION (§21.3.4d): this is an input-validation
/// failure of a grouping utility -- the same category as NormativeFieldBagConstructionException.
/// It is NOT a fifth resolver exception type; the union stays 3 / 5 / 4 (§15).
/// </summary>
public sealed class LogicalRecordValidationException : ArgumentException
{
    // Privately-typed canonical state. No code outside this class can NAME this type, therefore
    // no code outside this class can invoke the constructor.
    private readonly record struct Canonical(
        LogicalRecordValidationKind Kind, string ErrorCode, string ParamName,
        int? OffendingValue, RuleRoleSlot? OffendingRoleSlot, string Message);

    private readonly string _message;

    private LogicalRecordValidationException(Canonical c) : base(c.Message, c.ParamName)
    {
        Kind = c.Kind;
        ErrorCode = c.ErrorCode;
        OffendingValue = c.OffendingValue;
        OffendingRoleSlot = c.OffendingRoleSlot;
        _message = c.Message;
    }

    // ---------------------------------------------------------------------------------
    // The ONLY construction paths: 15 named factories over 14 validation identities.
    // OFFENDER_SHAPE_IS_KIND_DERIVED -- each factory's SIGNATURE encodes its offender shape, so
    // a Kind/offender mismatch is structurally unconstructible rather than merely forbidden.
    // ---------------------------------------------------------------------------------

    // no offender (5)
    internal static LogicalRecordValidationException NullFieldsArgument() => Make(LogicalRecordValidationKind.NullFieldsArgument);
    internal static LogicalRecordValidationException NullRuleArgument() => Make(LogicalRecordValidationKind.NullRuleArgument);
    internal static LogicalRecordValidationException NullViewArgument() => Make(LogicalRecordValidationKind.NullViewArgument);
    internal static LogicalRecordValidationException RuleRolesEqual() => Make(LogicalRecordValidationKind.RuleRolesEqual);
    internal static LogicalRecordValidationException NullFieldElement() => Make(LogicalRecordValidationKind.NullFieldElement);

    // role-slot offender: ONE identity, TWO entry points -- the factory assigns the literal slot,
    // so no caller supplies a RuleRoleSlot.
    internal static LogicalRecordValidationException RuleRoleUninitializedSlotA() =>
        Make(LogicalRecordValidationKind.RuleRoleUninitialized, slot: RuleRoleSlot.A);
    internal static LogicalRecordValidationException RuleRoleUninitializedSlotB() =>
        Make(LogicalRecordValidationKind.RuleRoleUninitialized, slot: RuleRoleSlot.B);

    // numeric offender (8)
    internal static LogicalRecordValidationException RuleMaxMembers(int value) => Make(LogicalRecordValidationKind.RuleMaxMembers, value);
    internal static LogicalRecordValidationException RuleMaxDistanceNegative(int value) => Make(LogicalRecordValidationKind.RuleMaxDistanceNegative, value);
    internal static LogicalRecordValidationException RuleSeparatorPolicyUndefined(int value) => Make(LogicalRecordValidationKind.RuleSeparatorPolicyUndefined, value);
    internal static LogicalRecordValidationException FieldRoleUninitialized(int labelStart) => Make(LogicalRecordValidationKind.FieldRoleUninitialized, labelStart);
    internal static LogicalRecordValidationException NegativeLineIndex(int labelStart) => Make(LogicalRecordValidationKind.NegativeLineIndex, labelStart);
    internal static LogicalRecordValidationException LabelSpanOutOfView(int labelStart) => Make(LogicalRecordValidationKind.LabelSpanOutOfView, labelStart);
    internal static LogicalRecordValidationException ValueSpanOutOfView(int labelStart) => Make(LogicalRecordValidationKind.ValueSpanOutOfView, labelStart);
    internal static LogicalRecordValidationException DuplicateLabelStart(int labelStart) => Make(LogicalRecordValidationKind.DuplicateLabelStart, labelStart);

    private static LogicalRecordValidationException Make(
        LogicalRecordValidationKind kind, int? value = null, RuleRoleSlot? slot = null)
        => new(Canonicalize(kind, value, slot));

    // ---------------------------------------------------------------------------------
    // KIND_CANONICAL_SPEC (§21.3.4f) -- ONE private mapping from a valid Kind to its ErrorCode,
    // ParamName, offender shape and Message template. Canonicalize consults it ONCE and returns
    // the complete record; the constructor copies that record and derives nothing further.
    // There is exactly one such mapping in the architecture: it is NOT duplicated in
    // LogicalRecordGrouper, in the constructor, in a separate message builder, or in test code,
    // so no second copy can drift.
    //
    // ERRORCODE_AND_MESSAGE_CANNOT_DIVERGE (§21.3.4f) follows: every observable comes from this
    // one call, keyed by a Kind the factory fixed as a literal.
    // ---------------------------------------------------------------------------------
    private static Canonical Canonicalize(LogicalRecordValidationKind kind, int? value, RuleRoleSlot? slot)
    {
        var (code, param) = Spec(kind);
        return new Canonical(kind, code, param, value, slot, BuildMessage(kind, value, slot));
    }

    private static (string ErrorCode, string ParamName) Spec(LogicalRecordValidationKind kind) => kind switch
    {
        LogicalRecordValidationKind.NullFieldsArgument => ("GROUPER_NULL_ARGUMENT", "fields"),
        LogicalRecordValidationKind.NullRuleArgument => ("GROUPER_NULL_ARGUMENT", "rule"),
        LogicalRecordValidationKind.NullViewArgument => ("GROUPER_NULL_ARGUMENT", "view"),
        LogicalRecordValidationKind.RuleRoleUninitialized => ("GROUPER_RULE_ROLE_UNINITIALIZED", "rule"),
        LogicalRecordValidationKind.RuleRolesEqual => ("GROUPER_RULE_ROLES_EQUAL", "rule"),
        LogicalRecordValidationKind.RuleMaxMembers => ("GROUPER_RULE_MAX_MEMBERS", "rule"),
        LogicalRecordValidationKind.RuleMaxDistanceNegative => ("GROUPER_RULE_MAX_DISTANCE_NEGATIVE", "rule"),
        LogicalRecordValidationKind.RuleSeparatorPolicyUndefined => ("GROUPER_RULE_SEPARATOR_POLICY_UNDEFINED", "rule"),
        LogicalRecordValidationKind.NullFieldElement => ("GROUPER_NULL_FIELD_ELEMENT", "fields"),
        LogicalRecordValidationKind.FieldRoleUninitialized => ("GROUPER_FIELD_ROLE_UNINITIALIZED", "fields"),
        LogicalRecordValidationKind.NegativeLineIndex => ("GROUPER_NEGATIVE_LINE_INDEX", "fields"),
        LogicalRecordValidationKind.LabelSpanOutOfView => ("GROUPER_LABEL_SPAN_OUT_OF_VIEW", "fields"),
        LogicalRecordValidationKind.ValueSpanOutOfView => ("GROUPER_VALUE_SPAN_OUT_OF_VIEW", "fields"),
        LogicalRecordValidationKind.DuplicateLabelStart => ("GROUPER_DUPLICATE_LABEL_START", "fields"),
        _ => throw Unreachable(),
    };

    /// <summary>
    /// PRIVATE STATIC and inside this type only -- never internal, never public, and never owned
    /// by LogicalRecordGrouper.cs, so no caller outside this type can generate or substitute
    /// normative Message text. Exhaustive over exactly the fourteen valid Kinds: no arbitrary
    /// default string, no kind.ToString(), no "UNKNOWN" fallback, no empty-string arm, and no
    /// culture-sensitive conversion.
    ///
    /// Every integer renders with CultureInfo.InvariantCulture: measured under ar-SA,
    /// (-7).ToString() yields U+061C followed by "-7" -- an invisible bidi control character in a
    /// public Message -- while the invariant conversion stays "-7". Classes 7 and 8 report values
    /// negative by definition and class 6 may, so this is a live path, not a precaution.
    /// RuleRoleSlot renders as the literal ASCII A / B, never slot.ToString().
    /// </summary>
    private static string BuildMessage(LogicalRecordValidationKind kind, int? value, RuleRoleSlot? slot) => kind switch
    {
        LogicalRecordValidationKind.NullFieldsArgument => "GROUPER_NULL_ARGUMENT: parameter=fields",
        LogicalRecordValidationKind.NullRuleArgument => "GROUPER_NULL_ARGUMENT: parameter=rule",
        LogicalRecordValidationKind.NullViewArgument => "GROUPER_NULL_ARGUMENT: parameter=view",
        LogicalRecordValidationKind.RuleRoleUninitialized =>
            "GROUPER_RULE_ROLE_UNINITIALIZED: role=" + (slot == RuleRoleSlot.A ? "A" : "B"),
        LogicalRecordValidationKind.RuleRolesEqual => "GROUPER_RULE_ROLES_EQUAL",
        LogicalRecordValidationKind.RuleMaxMembers => "GROUPER_RULE_MAX_MEMBERS: value=" + Int(value),
        LogicalRecordValidationKind.RuleMaxDistanceNegative => "GROUPER_RULE_MAX_DISTANCE_NEGATIVE: value=" + Int(value),
        LogicalRecordValidationKind.RuleSeparatorPolicyUndefined => "GROUPER_RULE_SEPARATOR_POLICY_UNDEFINED: value=" + Int(value),
        LogicalRecordValidationKind.NullFieldElement => "GROUPER_NULL_FIELD_ELEMENT",
        LogicalRecordValidationKind.FieldRoleUninitialized => "GROUPER_FIELD_ROLE_UNINITIALIZED: labelStart=" + Int(value),
        LogicalRecordValidationKind.NegativeLineIndex => "GROUPER_NEGATIVE_LINE_INDEX: labelStart=" + Int(value),
        LogicalRecordValidationKind.LabelSpanOutOfView => "GROUPER_LABEL_SPAN_OUT_OF_VIEW: labelStart=" + Int(value),
        LogicalRecordValidationKind.ValueSpanOutOfView => "GROUPER_VALUE_SPAN_OUT_OF_VIEW: labelStart=" + Int(value),
        LogicalRecordValidationKind.DuplicateLabelStart => "GROUPER_DUPLICATE_LABEL_START: labelStart=" + Int(value),
        _ => throw Unreachable(),
    };

    private static string Int(int? value) => value!.Value.ToString(CultureInfo.InvariantCulture);

    // Unreachable by UNKNOWN_KIND_IS_UNREPRESENTABLE. Deliberately NOT a public normative failure
    // outcome: it has no ErrorCode, no Kind, and no test obligation.
    private static InvalidOperationException Unreachable() =>
        new("Unreachable: LogicalRecordValidationKind is unrepresentable outside the closed factory surface.");

    // ---------------------------------------------------------------------------------
    // The six public observables -- ALL get-only, assigned once in the private constructor.
    // No setter, no init, no post-construction mutation.
    // ---------------------------------------------------------------------------------

    /// <summary>The machine-readable violation identity; one of the closed fourteen.</summary>
    public LogicalRecordValidationKind Kind { get; }

    /// <summary>
    /// Derived from KIND_CANONICAL_SPEC -- never parsed from <see cref="Message"/>, never
    /// inferred from the enum name, never caller-supplied, never settable.
    /// </summary>
    public string ErrorCode { get; }

    /// <summary>Genuine Nullable&lt;int&gt;; null means ABSENT. No sentinel: not -1, not 0, not int.MinValue.</summary>
    public int? OffendingValue { get; }

    /// <summary>Genuine Nullable&lt;T&gt;; null means ABSENT. Populated only for RuleRoleUninitialized.</summary>
    public RuleRoleSlot? OffendingRoleSlot { get; }

    /// <summary>The complete PRIVON-owned message -- no framework suffix, no second line, no localization.</summary>
    public override string Message => _message;
}
