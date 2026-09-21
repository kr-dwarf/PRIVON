namespace Privon.Detection;

/// <summary>D3B0-A A.9 §13 -- closed owner-claim domain; CCSK field 11 frozen positions 0, 1, 2.</summary>
public enum OwnerClaim
{
    NotApplicable = 0,
    False = 1,
    True = 2,
}
