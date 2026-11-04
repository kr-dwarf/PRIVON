namespace Privon.Detection;

/// <summary>
/// D3B0-A A.9 §22.3 -- the frozen 1:1 family and token-stem table for all 21 semantic types.
///
/// Same hand-written discipline as <see cref="PiiTypeIdCodec"/>: literal switch arms, never
/// <c>enum.ToString()</c>, reflection, or any naming heuristic. The nine legacy stems are the
/// existing <see cref="AliasLabelProvider"/> values; the twelve new stems come from D2.1 and the
/// frozen fixture tuples.
///
/// The stem table is INJECTIVE, which is what makes
/// <see cref="AliasIdentity.CanonicalContractToken"/> injective and therefore keeps identity
/// equality and CCSK field 10 ordering consistent (§22.4).
/// </summary>
public static class AliasFamilyTable
{
    /// <summary>The language-neutral family for a semantic type. Never a localized string.</summary>
    public static AliasFamilyId FamilyOf(PiiType type) => new(type switch
    {
        PiiType.Phone => "Phone",
        PiiType.Email => "Email",
        PiiType.ResidentRegistrationNumber => "ResidentRegistrationNumber",
        PiiType.CardNumber => "CardNumber",
        PiiType.BankAccountNumber => "BankAccountNumber",
        PiiType.Secret => "Secret",
        PiiType.IpAddress => "IpAddress",
        PiiType.MacAddress => "MacAddress",
        PiiType.GpsCoordinate => "GpsCoordinate",
        PiiType.Name => "Name",
        PiiType.Address => "Address",
        PiiType.UsSsn => "UsSsn",
        PiiType.UsItin => "UsItin",
        PiiType.UsAtin => "UsAtin",
        PiiType.UsMbi => "UsMbi",
        PiiType.UsPassport => "UsPassport",
        PiiType.UsANumber => "UsANumber",
        PiiType.UsI94 => "UsI94",
        PiiType.UsSevis => "UsSevis",
        PiiType.UsDriverStateId => "UsDriverStateId",
        PiiType.BankAccount => "BankAccount",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "No alias family is defined for this PiiType."),
    });

    /// <summary>
    /// §22.3 frozen token stem for a family. Locale-independent by construction: these are
    /// contract literals, not localization -- they never vary with UI language, culture, or
    /// settings.
    /// </summary>
    public static string FrozenTokenStem(AliasFamilyId family) => family.HasValue
        ? family.Value switch
        {
            "Phone" => "전화번호",
            "Email" => "이메일",
            "ResidentRegistrationNumber" => "주민등록번호",
            "CardNumber" => "카드번호",
            "BankAccountNumber" => "계좌번호",
            "Secret" => "비밀정보",
            "IpAddress" => "IP주소",
            "MacAddress" => "MAC주소",
            "GpsCoordinate" => "GPS좌표",
            "Name" => "이름",
            "Address" => "주소",
            "UsSsn" => "SSN",
            "UsItin" => "ITIN",
            "UsAtin" => "ATIN",
            "UsMbi" => "MBI",
            "UsPassport" => "여권번호",
            "UsANumber" => "A-NUMBER",
            "UsI94" => "I-94",
            "UsSevis" => "SEVIS",
            "UsDriverStateId" => "운전면허번호",
            "BankAccount" => "계좌",
            _ => throw new ArgumentOutOfRangeException(nameof(family), family.Value, "No frozen token stem is defined for this alias family."),
        }
        : throw new ArgumentOutOfRangeException(nameof(family), "default(AliasFamilyId) has no frozen token stem.");
}
