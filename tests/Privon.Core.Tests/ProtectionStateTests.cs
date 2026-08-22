using Privon.Core;

namespace Privon.Core.Tests;

public class ProtectionStateTests
{
    // Test 4: ProtectionState 기본값이 안전함 -- default(ProtectionState) must never
    // claim protection, and only Verified may.
    [Fact]
    public void DefaultProtectionState_DoesNotClaimProtected()
    {
        Assert.False(default(ProtectionState).ClaimsProtected());
        Assert.Equal(ProtectionState.Initializing, default(ProtectionState));
    }

    [Theory]
    [InlineData(ProtectionState.Initializing)]
    [InlineData(ProtectionState.Ready)]
    [InlineData(ProtectionState.Scanning)]
    [InlineData(ProtectionState.NeedsDecision)]
    [InlineData(ProtectionState.Protecting)]
    [InlineData(ProtectionState.Blocked)]
    [InlineData(ProtectionState.Limited)]
    [InlineData(ProtectionState.Paused)]
    [InlineData(ProtectionState.Error)]
    public void OnlyVerified_ClaimsProtected(ProtectionState state)
    {
        Assert.False(state.ClaimsProtected());
    }

    [Fact]
    public void Verified_ClaimsProtected()
    {
        Assert.True(ProtectionState.Verified.ClaimsProtected());
    }
}
