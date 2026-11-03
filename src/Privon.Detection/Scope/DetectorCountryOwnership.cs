namespace Privon.Detection.Scope;

/// <summary>
/// The approved detector-id -> country-pack ownership table. A literal switch over exact ids: no prefix inference, no
/// reflection, no registry or rank lookup. The U.S. members are exactly the thirteen frozen D2.2 identities that
/// <see cref="UsSupportedScope"/> also lists. Any other id, including every Global (<c>global.*</c>) and legacy Korean
/// (<c>kr.*</c>) id, has NO owner here and therefore always runs.
/// </summary>
internal static class DetectorCountryOwnership
{
    public static bool TryGetOwner(DetectorId id, out CountryPack owner)
    {
        owner = default;
        if (!id.HasValue) return false;

        switch (id.Value)
        {
            case "us.ssn":
            case "us.itin":
            case "us.atin":
            case "us.mbi":
            case "us.passport":
            case "us.a_number":
            case "us.i94":
            case "us.sevis":
            case "us.driver_state_id":
            case "us.phone":
            case "us.address":
            case "us.name":
            case "us.bank_account":
                owner = CountryPack.UnitedStates;
                return true;
            default:
                return false;
        }
    }
}
