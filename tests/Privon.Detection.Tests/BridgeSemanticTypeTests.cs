using Privon.Detection;
using Xunit;

namespace Privon.Detection.Tests;

/// <summary>
/// D3B0-B §1 / §32 -- semantic-type extensibility and codec/persistence compatibility.
///
/// The bridge appends twelve PiiType members. These tests prove the append is PERSISTENCE-SAFE:
/// every pre-existing stable ID is byte-unchanged and round-trips exactly as before, so no old
/// persisted data can be reinterpreted.
/// </summary>
public sealed class BridgeSemanticTypeTests
{
    // The nine frozen 0.3.x identities, written out literally rather than derived from the enum,
    // so a rename/reorder of PiiType cannot make this test vacuously agree with itself.
    private static readonly (PiiType Type, string StableId)[] FrozenNine =
    [
        (PiiType.Phone, "Phone"),
        (PiiType.Email, "Email"),
        (PiiType.ResidentRegistrationNumber, "ResidentRegistrationNumber"),
        (PiiType.CardNumber, "CardNumber"),
        (PiiType.BankAccountNumber, "BankAccountNumber"),
        (PiiType.Secret, "Secret"),
        (PiiType.IpAddress, "IpAddress"),
        (PiiType.MacAddress, "MacAddress"),
        (PiiType.GpsCoordinate, "GpsCoordinate"),
    ];

    private static readonly string[] TwelveAppended =
    [
        "Name", "Address", "UsSsn", "UsItin", "UsAtin", "UsMbi",
        "UsPassport", "UsANumber", "UsI94", "UsSevis", "UsDriverStateId", "BankAccount",
    ];

    [Fact]
    public void EveryPreExistingStableId_IsByteUnchanged()
    {
        foreach (var (type, expected) in FrozenNine)
            Assert.Equal(expected, PiiTypeIdCodec.ToStableId(type));
    }

    [Fact]
    public void EveryPreExistingStableId_RoundTripsToTheSameType()
    {
        foreach (var (type, stableId) in FrozenNine)
        {
            Assert.True(PiiTypeIdCodec.TryFromStableId(stableId, out var back));
            Assert.Equal(type, back);
        }
    }

    [Fact]
    public void PreExistingOrdinals_AreUnchangedByTheAppend()
    {
        // PII_TYPE_APPEND_ONLY: ordinals 0..8 stay fixed and the twelve appended take 9..20.
        Assert.Equal(0, (int)PiiType.Phone);
        Assert.Equal(1, (int)PiiType.Email);
        Assert.Equal(2, (int)PiiType.ResidentRegistrationNumber);
        Assert.Equal(3, (int)PiiType.CardNumber);
        Assert.Equal(4, (int)PiiType.BankAccountNumber);
        Assert.Equal(5, (int)PiiType.Secret);
        Assert.Equal(6, (int)PiiType.IpAddress);
        Assert.Equal(7, (int)PiiType.MacAddress);
        Assert.Equal(8, (int)PiiType.GpsCoordinate);
        Assert.Equal(9, (int)PiiType.Name);
        Assert.Equal(20, (int)PiiType.BankAccount);
    }

    [Fact]
    public void SemanticTypeDomain_IsExactlyTwentyOne_AndCodecIsTotalBothDirections()
    {
        var all = Enum.GetValues<PiiType>();
        Assert.Equal(21, all.Length);

        foreach (var type in all)
        {
            string id = PiiTypeIdCodec.ToStableId(type);
            Assert.True(PiiTypeIdCodec.TryFromStableId(id, out var back));
            Assert.Equal(type, back);
        }
    }

    [Fact]
    public void TwelveAppendedStableIds_AreExactlyTheAcceptedLiterals()
    {
        foreach (string id in TwelveAppended)
            Assert.True(PiiTypeIdCodec.TryFromStableId(id, out _), id);
    }

    [Fact]
    public void UnknownStableId_IsNeverGuessed()
    {
        // Matching stays exact, ordinal, case-sensitive and un-trimmed.
        foreach (string bad in new[] { "phone", "PHONE", " Phone ", "Phone\n", "", "UsSSN", "Unknown" })
            Assert.False(PiiTypeIdCodec.TryFromStableId(bad, out _), bad);

        Assert.False(PiiTypeIdCodec.TryFromStableId(null, out _));
    }

    [Fact]
    public void UndefinedPiiTypeCast_FailsClosed_AndIsNeverInvented()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PiiTypeIdCodec.ToStableId((PiiType)999));
    }

    [Fact]
    public void EveryPiiType_HasExactlyOneAliasFamilyAndOneFrozenStem()
    {
        // PII_TYPE_CODEC_TOTALITY + §22.3: 1:1 and injective across all 21.
        var stems = new HashSet<string>(StringComparer.Ordinal);
        var families = new HashSet<string>(StringComparer.Ordinal);

        foreach (var type in Enum.GetValues<PiiType>())
        {
            var family = AliasFamilyTable.FamilyOf(type);
            Assert.True(families.Add(family.Value), "family not injective: " + family.Value);

            string stem = AliasFamilyTable.FrozenTokenStem(family);
            Assert.False(string.IsNullOrEmpty(stem));
            Assert.True(stems.Add(stem), "stem not injective: " + stem);
        }
        Assert.Equal(21, families.Count);
        Assert.Equal(21, stems.Count);
    }

    [Fact]
    public void LegacyAliasLabels_AreUnchanged_AndAgreeWithTheFrozenStemTable()
    {
        // The nine legacy stems must remain exactly what AliasLabelProvider already produced.
        Assert.Equal("전화번호", AliasLabelProvider.GetLabel(PiiType.Phone));
        Assert.Equal("이메일", AliasLabelProvider.GetLabel(PiiType.Email));
        Assert.Equal("GPS좌표", AliasLabelProvider.GetLabel(PiiType.GpsCoordinate));

        foreach (var type in Enum.GetValues<PiiType>())
            Assert.Equal(
                AliasLabelProvider.GetLabel(type),
                AliasFamilyTable.FrozenTokenStem(AliasFamilyTable.FamilyOf(type)));
    }
}
