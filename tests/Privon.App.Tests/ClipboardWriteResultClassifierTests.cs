using Privon.App;
using Privon.Windows;

namespace Privon.App.Tests;

// Phase 3B STEP14 -- ClipboardWriteResultClassifier regression. A pure function -- no fakes
// needed (same style as TargetGateTests). WRITE_OUTCOME_CLASSIFICATION (Phase 3B STEP13 audit,
// frozen): classification is authoritative from (Outcome, ClipboardMutated) together, never from
// the Outcome enum name alone.
public class ClipboardWriteResultClassifierTests
{
    // ---- 17. Success + ClipboardMutated true -> verified rewrite (the only true case) ----
    [Fact]
    public void IsRewriteVerified_SuccessWithMutation_ReturnsTrue()
    {
        var result = ClipboardWriteResult.Success(resultSequence: 42);

        Assert.True(ClipboardWriteResultClassifier.IsRewriteVerified(result));
    }

    // ---- 18. NativeFailure + ClipboardMutated false -> not verified (no mutation at all) ----
    [Fact]
    public void IsRewriteVerified_NativeFailureNoMutation_ReturnsFalse()
    {
        var result = ClipboardWriteResult.Failure(ClipboardWriteOutcome.NativeFailure, mutated: false, win32Error: 5);

        Assert.False(ClipboardWriteResultClassifier.IsRewriteVerified(result));
    }

    // ---- 19. NativeFailure + ClipboardMutated true -> not verified (mutated-unverified) ----
    [Fact]
    public void IsRewriteVerified_NativeFailureWithMutation_ReturnsFalse()
    {
        var result = ClipboardWriteResult.Failure(ClipboardWriteOutcome.NativeFailure, mutated: true, win32Error: 5);

        Assert.False(ClipboardWriteResultClassifier.IsRewriteVerified(result));
    }

    // ---- 20. VerificationUnavailable -> mutated-unverified, not verified ----
    [Fact]
    public void IsRewriteVerified_VerificationUnavailable_ReturnsFalse()
    {
        var result = ClipboardWriteResult.Failure(ClipboardWriteOutcome.VerificationUnavailable, mutated: true);

        Assert.False(ClipboardWriteResultClassifier.IsRewriteVerified(result));
    }

    // ---- 21. Superseded -> mutated, then clipboard moved on -- not verified ----
    [Fact]
    public void IsRewriteVerified_Superseded_ReturnsFalse()
    {
        var result = ClipboardWriteResult.Failure(ClipboardWriteOutcome.Superseded, mutated: true);

        Assert.False(ClipboardWriteResultClassifier.IsRewriteVerified(result));
    }

    // ---- 22. ReadBackMismatch -> mutated, verification mismatch -- not verified ----
    [Fact]
    public void IsRewriteVerified_ReadBackMismatch_ReturnsFalse()
    {
        var result = ClipboardWriteResult.Failure(ClipboardWriteOutcome.ReadBackMismatch, mutated: true, resultSequence: 42);

        Assert.False(ClipboardWriteResultClassifier.IsRewriteVerified(result));
    }

    // ---- every remaining no-mutation abort path is also never verified ----
    [Theory]
    [InlineData(ClipboardWriteOutcome.NotRunning)]
    [InlineData(ClipboardWriteOutcome.InvalidExpectedSequence)]
    [InlineData(ClipboardWriteOutcome.InvalidText)]
    [InlineData(ClipboardWriteOutcome.Busy)]
    [InlineData(ClipboardWriteOutcome.SequenceChanged)]
    [InlineData(ClipboardWriteOutcome.InvalidExpectedTarget)]
    [InlineData(ClipboardWriteOutcome.TargetUnavailable)]
    [InlineData(ClipboardWriteOutcome.TargetChanged)]
    public void IsRewriteVerified_ExpectedAbortPaths_ReturnFalse(ClipboardWriteOutcome outcome)
    {
        var result = ClipboardWriteResult.Failure(outcome, mutated: false);

        Assert.False(ClipboardWriteResultClassifier.IsRewriteVerified(result));
    }

    [Fact]
    public void ClipboardWriteResultClassifier_IsInternal()
    {
        Assert.False(typeof(ClipboardWriteResultClassifier).IsPublic);
    }
}
