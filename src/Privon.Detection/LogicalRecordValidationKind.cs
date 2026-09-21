namespace Privon.Detection;

/// <summary>
/// D3B0-A A.9 §21.3.4b -- closed violation identity, exactly 14 members, one per accepted
/// LogicalRecordGrouper validation class, in the frozen GROUPER_VALIDATION_PRECEDENCE order
/// (§21.3.4): argument existence (1-3), rule well-formedness (4-8), element well-formedness
/// (9-13), set uniqueness (14).
///
/// UNKNOWN_KIND_IS_UNREPRESENTABLE: no accessible surface accepts this type as a parameter, so
/// an undefined cast cannot cross the construction boundary (see LogicalRecordValidationException).
/// No None/Unknown/Default member exists.
/// </summary>
public enum LogicalRecordValidationKind
{
    NullFieldsArgument = 1,
    NullRuleArgument = 2,
    NullViewArgument = 3,
    RuleRoleUninitialized = 4,
    RuleRolesEqual = 5,
    RuleMaxMembers = 6,
    RuleMaxDistanceNegative = 7,
    RuleSeparatorPolicyUndefined = 8,
    NullFieldElement = 9,
    FieldRoleUninitialized = 10,
    NegativeLineIndex = 11,
    LabelSpanOutOfView = 12,
    ValueSpanOutOfView = 13,
    DuplicateLabelStart = 14,
}
