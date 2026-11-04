namespace Privon.Detection;

/// <summary>
/// D3B0-A A.9 §21.3.4b -- closed two-member role slot; the only offender shape that is not an
/// integer. It is the public domain of
/// <see cref="LogicalRecordValidationException.OffendingRoleSlot"/>.
///
/// INVALID_ROLE_SLOT_IS_UNREPRESENTABLE: no accessible factory accepts this type as a
/// parameter -- the two role-slot factories assign the literal themselves -- so an undefined
/// cast has no entry path.
/// </summary>
public enum RuleRoleSlot
{
    A = 1,
    B = 2,
}
