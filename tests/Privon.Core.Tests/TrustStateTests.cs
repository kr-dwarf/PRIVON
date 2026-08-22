using Privon.Core;

namespace Privon.Core.Tests;

public class TrustStateTests
{
    // Test 3: unknown 상태가 trusted로 처리되지 않음.
    [Fact]
    public void Unknown_IsNotTrusted()
    {
        Assert.False(TrustState.Unknown.IsTrusted());
    }

    [Fact]
    public void DefaultTrustState_IsUnknown()
    {
        Assert.Equal(TrustState.Unknown, default(TrustState));
    }

    [Fact]
    public void Untrusted_IsNotTrusted()
    {
        Assert.False(TrustState.Untrusted.IsTrusted());
    }

    [Fact]
    public void OnlyTrusted_IsTrusted()
    {
        Assert.True(TrustState.Trusted.IsTrusted());
    }
}
