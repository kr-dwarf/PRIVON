namespace Privon.Detection;

/// <summary>
/// Phase 2T.1 -- explicit, hand-maintained mapping from PiiType to the Korean noun used in its
/// alias token (e.g. PiiType.Phone -> "전화번호", so the assigned token reads "[전화번호1]").
/// Same discipline as <see cref="PiiTypeIdCodec"/>: a plain <c>switch</c> over literal cases,
/// never <c>enum.ToString()</c>, reflection, or any other naming-heuristic/localization-
/// inference mechanism that could silently produce the wrong label (or drift) if PiiType is
/// ever renamed or reordered.
///
/// Labels for Phone/Email come directly from the design doc's own "가명 규칙" example
/// (`[전화번호1]`, `[이메일1]`). The remaining seven (ResidentRegistrationNumber, CardNumber,
/// BankAccountNumber, Secret, IpAddress, MacAddress, GpsCoordinate) had no Source-of-Truth
/// label until this phase -- see the Phase 2T report's PIITYPE_ALIAS_LABEL_GAP finding -- and
/// are fixed here as an explicit product decision: 주민등록번호/카드번호/계좌번호/비밀정보/
/// IP주소/MAC주소/GPS좌표.
///
/// Note "비밀정보" (Secret) supersedes the earlier "[시크릿1]" name SecretDetector's own
/// AlreadyProtectedAliasToken constant recognized -- that constant was never a Source-of-Truth
/// label (its own comment already said so), only an internal "already protected, don't
/// re-detect" convention, and recognizing a "[시크릿1]"-shaped token as already-protected is
/// still harmless even though this provider now only ever *produces* "[비밀정보N]" going
/// forward. SecretDetector itself is not changed by this phase.
/// </summary>
public static class AliasLabelProvider
{
    public static string GetLabel(PiiType type) => type switch
    {
        PiiType.Phone => "전화번호",
        PiiType.Email => "이메일",
        PiiType.ResidentRegistrationNumber => "주민등록번호",
        PiiType.CardNumber => "카드번호",
        PiiType.BankAccountNumber => "계좌번호",
        PiiType.Secret => "비밀정보",
        PiiType.IpAddress => "IP주소",
        PiiType.MacAddress => "MAC주소",
        PiiType.GpsCoordinate => "GPS좌표",
        // ---- appended by D3B0 (A.9 §22.3). The nine above are unchanged; this provider and
        // ---- AliasFamilyTable.FrozenTokenStem hold the SAME frozen stems for all 21 types.
        PiiType.Name => "이름",
        PiiType.Address => "주소",
        PiiType.UsSsn => "SSN",
        PiiType.UsItin => "ITIN",
        PiiType.UsAtin => "ATIN",
        PiiType.UsMbi => "MBI",
        PiiType.UsPassport => "여권번호",
        PiiType.UsANumber => "A-NUMBER",
        PiiType.UsI94 => "I-94",
        PiiType.UsSevis => "SEVIS",
        PiiType.UsDriverStateId => "운전면허번호",
        PiiType.BankAccount => "계좌",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "No alias label is defined for this PiiType."),
    };
}
