using Privon.Detection;
using Xunit;

namespace Privon.Detection.Tests;

/// <summary>D3B0-B §23 / §24 -- NormativeFieldName and NormativeFieldBag.</summary>
public sealed class BridgeNormativeFieldTests
{
    [Fact]
    public void TryCreate_AcceptsTheDomain_AndPreservesExactCasing()
    {
        foreach (string ok in new[] { "A", "z", "DetectorId2", "Experimental2", new string('a', 64) })
        {
            Assert.True(NormativeFieldName.TryCreate(ok, out var name), ok);
            Assert.Equal(ok, name.Value);          // no normalization, no trimming, no case folding
            Assert.True(name.HasValue);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData(" Lead")]
    [InlineData("Trail ")]
    [InlineData("has space")]
    [InlineData("under_score")]
    [InlineData("punct!")]
    [InlineData("1leading")]
    [InlineData("한글")]
    [InlineData("emoji\U0001F600")]
    [InlineData("\ud800")]
    public void TryCreate_Failure_ReturnsFalseAndDefault_AndNeverThrows(string? bad)
    {
        // TRYCREATE_FAILURE_OUT_IS_DEFAULT -- exactly default(NormativeFieldName) on EVERY path.
        Assert.False(NormativeFieldName.TryCreate(bad, out var name));
        Assert.Equal(default, name);
        Assert.False(name.HasValue);
    }

    [Fact]
    public void TryCreate_65CharsIsInvalid_64IsValid()
    {
        Assert.True(NormativeFieldName.TryCreate(new string('a', 64), out _));
        Assert.False(NormativeFieldName.TryCreate(new string('a', 65), out var name));
        Assert.Equal(default, name);
    }

    [Fact]
    public void TryCreate_FailureOverwritesAPreviousSuccessfulOut()
    {
        Assert.True(NormativeFieldName.TryCreate("Valid", out var name));
        Assert.Equal("Valid", name.Value);

        // The failing call must OVERWRITE, never retain the previous value.
        Assert.False(NormativeFieldName.TryCreate("!invalid", out name));
        Assert.Equal(default, name);
    }

    [Fact]
    public void DefaultName_IsInvalid_AndNeverEqualsAValidName()
    {
        NormativeFieldName.TryCreate("Valid", out var valid);
        Assert.NotEqual(default, valid);
        Assert.False(default(NormativeFieldName).HasValue);
        Assert.Equal(default, default(NormativeFieldName));
    }

    [Fact]
    public void Bag_Create_SucceedsForWellFormedUnknownNames()
    {
        // UNKNOWN_FIELD_SURFACE_REMAINS_CONSTRUCTIBLE -- this is what makes R0.5 check 1 reachable.
        var bag = NormativeFieldBag.Create([new("SomeUnknownField", 1), new("X", "v")]);
        Assert.Equal(2, bag.Count);
        Assert.Equal(["SomeUnknownField", "X"], bag.Names.Select(n => n.Value).ToArray());
    }

    [Fact]
    public void Bag_NamesAreSortedIndependentlyOfInsertionOrder()
    {
        var forward = NormativeFieldBag.Create([new("Bbb", 1), new("Aaa", 2), new("Ccc", 3)]);
        var reverse = NormativeFieldBag.Create([new("Ccc", 3), new("Bbb", 1), new("Aaa", 2)]);
        Assert.Equal(forward.Names.Select(n => n.Value), reverse.Names.Select(n => n.Value));
        Assert.Equal(["Aaa", "Bbb", "Ccc"], forward.Names.Select(n => n.Value).ToArray());
    }

    [Fact]
    public void Bag_InvalidFieldName_WinsThePrecedence_AndSelectsCanonically()
    {
        // Class 1 beats class 2 and 3; the offender is the smallest key under null-first then
        // UTF-8 byte order -- identical under every permutation of the fields list.
        var fields = new List<KeyValuePair<string?, object?>>
        {
            new("zz!", 1), new("Dup", 2), new("Dup", 3), new("DetectorId", 4), new("aa!", 5),
        };

        for (int rotate = 0; rotate < fields.Count; rotate++)
        {
            var rotated = fields.Skip(rotate).Concat(fields.Take(rotate)).ToList();
            var ex = Assert.Throws<NormativeFieldBagConstructionException>(() => NormativeFieldBag.Create(rotated));
            Assert.Equal("NORMATIVE_FIELD_BAG_INVALID_FIELD_NAME", ex.ErrorCode);
            Assert.Equal("aa!", ex.OffendingName);
            Assert.Equal("fields", ex.ParamName);
            Assert.Equal("NORMATIVE_FIELD_BAG_INVALID_FIELD_NAME: name=aa!", ex.Message);
        }
    }

    [Fact]
    public void Bag_NullKey_IsSelectedFirst_AndRendersAsAngleBracketNull()
    {
        var ex = Assert.Throws<NormativeFieldBagConstructionException>(
            () => NormativeFieldBag.Create([new("zz!", 1), new(null, 2)]));
        Assert.Null(ex.OffendingName);
        Assert.Equal("NORMATIVE_FIELD_BAG_INVALID_FIELD_NAME: name=<null>", ex.Message);
    }

    [Fact]
    public void Bag_DuplicateFieldName_IsClass2()
    {
        var ex = Assert.Throws<NormativeFieldBagConstructionException>(
            () => NormativeFieldBag.Create([new("Bbb", 1), new("Aaa", 2), new("Aaa", 3), new("Bbb", 4)]));
        Assert.Equal("NORMATIVE_FIELD_BAG_DUPLICATE_FIELD_NAME", ex.ErrorCode);
        Assert.Equal("Aaa", ex.OffendingName);
    }

    [Theory]
    [InlineData("DetectorId")]
    [InlineData("SemanticType")]
    [InlineData("SpanStart")]
    [InlineData("SpanLength")]
    [InlineData("Span")]
    [InlineData("RiskLevel")]
    [InlineData("ResolutionEvidence")]
    [InlineData("DetectionConfidence")]
    [InlineData("ResolutionRank")]
    [InlineData("CanonicalValue")]
    [InlineData("AliasIdentity")]
    [InlineData("OwnerClaim")]
    public void Bag_EachOfTheTwelveReservedNames_IsRejected(string reserved)
    {
        var ex = Assert.Throws<NormativeFieldBagConstructionException>(
            () => NormativeFieldBag.Create([new(reserved, 1)]));
        Assert.Equal("NORMATIVE_FIELD_BAG_RECOGNIZED_NAME_COLLISION", ex.ErrorCode);
        Assert.Equal(reserved, ex.OffendingName);
    }

    [Fact]
    public void Bag_ConstructionFailure_IsOutsideTheResolverUnion()
    {
        var ex = Assert.Throws<NormativeFieldBagConstructionException>(
            () => NormativeFieldBag.Create([new("Span", 1)]));
        Assert.True(ex is ArgumentException);
        Assert.IsNotType<UnknownCandidateSemanticFieldException>(ex);
        Assert.DoesNotContain("(Parameter", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bag_Empty_IsCountZero()
    {
        Assert.Equal(0, NormativeFieldBag.Empty.Count);
        Assert.Empty(NormativeFieldBag.Empty.Names);
    }
}
