using Privon.Detection;

namespace Privon.Detection.Tests;

// Phase 2R.3 — PiiTypeIdCodec tests. All identifiers here are synthetic strings, never real
// PII. This file's real job is proving the codec never guesses: unknown/case-varied/padded
// strings must fail closed (return false), and every currently-defined PiiType must have an
// explicit, unique, round-trippable stable ID -- enforced as a regression so a future PiiType
// added without an accompanying codec entry turns this suite red.
public class PiiTypeIdCodecTests
{
    // ---- 1/2/3. every current PiiType round-trips through ToStableId/TryFromStableId ----
    [Theory]
    [InlineData(PiiType.Phone)]
    [InlineData(PiiType.Email)]
    [InlineData(PiiType.ResidentRegistrationNumber)]
    [InlineData(PiiType.CardNumber)]
    [InlineData(PiiType.BankAccountNumber)]
    [InlineData(PiiType.Secret)]
    [InlineData(PiiType.IpAddress)]
    [InlineData(PiiType.MacAddress)]
    [InlineData(PiiType.GpsCoordinate)]
    public void RoundTrips_ForEveryExplicitlyListedPiiType(PiiType type)
    {
        var id = PiiTypeIdCodec.ToStableId(type);
        Assert.False(string.IsNullOrWhiteSpace(id));

        Assert.True(PiiTypeIdCodec.TryFromStableId(id, out var restored));
        Assert.Equal(type, restored);
    }

    // ---- 12. enum-completeness regression: every CURRENT PiiType member must round-trip.
    // Iterating Enum.GetValues here is test-only verification, not a production mapping
    // mechanism -- if a future PiiType is added without a matching codec entry, ToStableId
    // throws and this test goes red immediately. ----
    [Fact]
    public void AllCurrentPiiTypeMembers_HaveExplicitMapping()
    {
        var allTypes = Enum.GetValues<PiiType>();
        Assert.NotEmpty(allTypes); // sanity: the enum itself isn't empty

        foreach (var type in allTypes)
        {
            var id = PiiTypeIdCodec.ToStableId(type); // throws if unmapped -- test goes red
            Assert.True(PiiTypeIdCodec.TryFromStableId(id, out var restored));
            Assert.Equal(type, restored);
        }
    }

    // ---- 4. every stable ID is unique across all current PiiType values ----
    [Fact]
    public void AllStableIds_AreUnique()
    {
        var ids = Enum.GetValues<PiiType>().Select(PiiTypeIdCodec.ToStableId).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    // ---- 5. unknown identifier -> false ----
    [Theory]
    [InlineData("FutureUnknownType")]
    [InlineData("Unknown")]
    [InlineData("Email2")]
    [InlineData("NotAnyRealType")]
    public void UnknownStableId_ReturnsFalse(string id)
    {
        Assert.False(PiiTypeIdCodec.TryFromStableId(id, out _));
    }

    // ---- 6. empty string -> false ----
    [Fact]
    public void EmptyStableId_ReturnsFalse()
    {
        Assert.False(PiiTypeIdCodec.TryFromStableId("", out _));
    }

    // ---- 7. whitespace-only -> false ----
    [Theory]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void WhitespaceOnlyStableId_ReturnsFalse(string id)
    {
        Assert.False(PiiTypeIdCodec.TryFromStableId(id, out _));
    }

    // ---- 8. null -> false, never throws ----
    [Fact]
    public void NullStableId_ReturnsFalse_DoesNotThrow()
    {
        Assert.False(PiiTypeIdCodec.TryFromStableId(null, out _));
    }

    // ---- 9. case variant -> false (exact-match only, no case-fold) ----
    [Theory]
    [InlineData("phone")]
    [InlineData("PHONE")]
    [InlineData("PhoNe")]
    public void CaseVariant_ReturnsFalse(string id)
    {
        Assert.False(PiiTypeIdCodec.TryFromStableId(id, out _));
    }

    // ---- 10. leading/trailing whitespace around an otherwise-valid id -> false (no trimming) ----
    [Theory]
    [InlineData(" Phone")]
    [InlineData("Phone ")]
    [InlineData(" Phone ")]
    public void PaddedStableId_ReturnsFalse(string id)
    {
        Assert.False(PiiTypeIdCodec.TryFromStableId(id, out _));
    }

    // ---- 11. undefined enum value -> ToStableId fails fast, never invents an identifier ----
    [Fact]
    public void UndefinedEnumValue_ToStableId_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PiiTypeIdCodec.ToStableId((PiiType)999));
    }

    // ---- 13. (structural note, not runtime-testable) production ToStableId/TryFromStableId
    // are explicit switch expressions over literal string cases -- no Enum.Parse, no
    // Enum.TryParse, no .ToString(), no reflection anywhere in PiiTypeIdCodec.cs. Verified by
    // inspection of the source file; this comment exists so the invariant is documented next
    // to the tests that would catch its violation (wrong case handling, unknown-id guessing). ----

    // ---- Storage boundary: the codec is exercised with plain synthetic strings only --
    // Privon.Detection does not reference Privon.Storage anywhere, including in this test
    // project (no ExceptionEntry/TrustedPublicInfoEntry usage here). The real Storage<->
    // Detection bridge is a future integration-phase responsibility. ----
    [Fact]
    public void CodecWorksWithPlainSyntheticStrings_NoStorageTypesInvolved()
    {
        Assert.True(PiiTypeIdCodec.TryFromStableId("Phone", out var type));
        Assert.Equal(PiiType.Phone, type);
    }

    // ---- 14. synthetic-only -- every identifier used above is either a real stable ID this
    // codec defines or an obviously-fabricated placeholder string; no real PII anywhere. ----
}
