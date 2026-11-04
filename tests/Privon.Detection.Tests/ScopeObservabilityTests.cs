using System.Reflection;
using Privon.Detection;
using Privon.Detection.Scope;
using Xunit;

namespace Privon.Detection.Tests;

/// <summary>
/// D3B0-A A.17 §16.7 structural tests for the supported-scope observation surface. Every query
/// identity here is a synthetic lexical value; none is a D3A CaseId or an A1.9 TABLE T-SCOPE row.
/// </summary>
public class ScopeObservabilityTests
{
    private static readonly string[] SupportedUs =
    [
        "us.ssn", "us.itin", "us.atin", "us.mbi", "us.passport", "us.a_number", "us.i94",
        "us.sevis", "us.driver_state_id", "us.phone", "us.address", "us.name", "us.bank_account",
    ];

    private static ScopeIdentity Id(string value)
    {
        Assert.True(ScopeIdentity.TryCreate(value, out var id), value);
        return id;
    }

    [Theory]
    [InlineData("us.ssn")]
    [InlineData("us.driver_state_id")]
    [InlineData("us.foo")]
    [InlineData("global.email")]
    [InlineData("a.b")]
    [InlineData("a1.b2_c3")]
    public void TryCreate_AcceptsTheGrammar(string value)
    {
        Assert.True(ScopeIdentity.TryCreate(value, out var id));
        Assert.True(id.HasValue);
        Assert.Equal(value, id.Value);
        Assert.Equal(value, id.ToString());
    }

    [Theory]
    [InlineData("foo")]            // no namespace
    [InlineData("US.SSN")]         // upper case
    [InlineData("us.foo-bar")]     // hyphen
    [InlineData(" us.foo")]        // leading whitespace
    [InlineData("us.foo ")]        // trailing whitespace
    [InlineData("us. foo")]        // internal whitespace
    [InlineData("us..foo")]        // empty segment
    [InlineData("us.foo.bar")]     // two dots
    [InlineData(".foo")]
    [InlineData("us.")]
    [InlineData("1us.foo")]        // segment must start with a letter
    [InlineData("us._foo")]
    [InlineData("us.foo_")]
    [InlineData("us.foo__bar")]
    [InlineData("us.ｆoo")]         // non-ASCII
    public void TryCreate_RejectsMalformedValues_WithoutRepair(string value)
    {
        Assert.False(ScopeIdentity.TryCreate(value, out var id));
        Assert.False(id.HasValue);
        Assert.Equal(default, id);
    }

    [Fact]
    public void TryCreate_RejectsNullAndEmpty()
    {
        Assert.False(ScopeIdentity.TryCreate(null, out var a));
        Assert.False(a.HasValue);
        Assert.False(ScopeIdentity.TryCreate(string.Empty, out var b));
        Assert.False(b.HasValue);
    }

    [Fact]
    public void MaxLength_Is64_AndTheBoundaryIsExact()
    {
        Assert.Equal(64, ScopeIdentity.MaxLength);

        string at64 = "us." + new string('a', 61);
        string at65 = "us." + new string('a', 62);
        Assert.Equal(64, at64.Length);
        Assert.Equal(65, at65.Length);

        Assert.True(ScopeIdentity.TryCreate(at64, out _));
        Assert.False(ScopeIdentity.TryCreate(at65, out _));
    }

    [Fact]
    public void Equality_IsOrdinal()
    {
        Assert.Equal(Id("us.ssn"), Id("us.ssn"));
        Assert.NotEqual(Id("us.ssn"), Id("us.itin"));
    }

    [Fact]
    public void Observe_ReportsEveryOneOfTheThirteenSupportedIdentities_AsSupported()
    {
        Assert.Equal(13, SupportedUs.Length);
        Assert.Equal(13, SupportedUs.Distinct(StringComparer.Ordinal).Count());
        foreach (var value in SupportedUs)
        {
            var identity = Id(value);
            var observation = UsSupportedScope.Observe(identity);
            Assert.True(observation.IsSupported, value);
            Assert.Equal(identity, observation.Identity);
        }
    }

    [Theory]
    [InlineData("us.foo")]
    [InlineData("us.anumber")]     // superseded D2.1 spelling; D2.2 authority is us.a_number
    [InlineData("global.email")]   // a detector-ID literal outside the U.S. scope domain
    [InlineData("kr.phone")]
    public void Observe_ReportsAWellFormedNonMember_AsUnsupported(string value)
    {
        var identity = Id(value);
        var observation = UsSupportedScope.Observe(identity);
        Assert.False(observation.IsSupported);
        Assert.Equal(identity, observation.Identity);
    }

    [Fact]
    public void Observe_Default_IsACallerArgumentError_NeverUnsupported()
    {
        var ex = Assert.Throws<ArgumentException>(() => UsSupportedScope.Observe(default));
        Assert.Equal("identity", ex.ParamName);
    }

    [Fact]
    public void ScopeIdentity_IsNotADetectorId_AndNoConversionExists()
    {
        Assert.NotEqual(typeof(DetectorId), typeof(ScopeIdentity));
        var conversions = typeof(ScopeIdentity)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Concat(typeof(DetectorId).GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.Name is "op_Implicit" or "op_Explicit")
            .Where(m => m.ReturnType == typeof(ScopeIdentity) || m.ReturnType == typeof(DetectorId))
            .Where(m => m.GetParameters().Any(p =>
                p.ParameterType == typeof(ScopeIdentity) || p.ParameterType == typeof(DetectorId)));
        Assert.Empty(conversions);
    }

    [Fact]
    public void Surface_IsExactlyAsDeclared()
    {
        Assert.True(typeof(ScopeIdentity).IsValueType);
        Assert.True(typeof(UsSupportedScope).IsAbstract && typeof(UsSupportedScope).IsSealed); // static
        Assert.True(typeof(ScopeSupportObservation).IsSealed);

        var observe = typeof(UsSupportedScope).GetMethod(nameof(UsSupportedScope.Observe))!;
        Assert.Equal(typeof(ScopeSupportObservation), observe.ReturnType);
        Assert.Equal(new[] { typeof(ScopeIdentity) }, observe.GetParameters().Select(p => p.ParameterType));

        // UsSupportedScope exposes Observe and nothing else: no public list, set or enumeration of
        // any identity, supported or otherwise.
        var publicMembers = typeof(UsSupportedScope)
            .GetMembers(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToArray();
        Assert.Equal(new[] { nameof(UsSupportedScope.Observe) }, publicMembers);
    }
}
