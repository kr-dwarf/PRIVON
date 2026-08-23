using Privon.App;
using Privon.Windows;

namespace Privon.App.Tests;

// Phase 0.2D (STEP61) -- exhaustive table tests for ClipboardEvaluationOutcomeClassifier against
// every ACTUAL current member of ClipboardReadOutcome/ClipboardWriteOutcome (re-read directly from
// their real source files before writing this table, not from memory or the STEP61 instruction's
// own restated list, per that instruction's own requirement).
public class ClipboardEvaluationOutcomeClassifierTests
{
    // ==================================================================
    // READ OUTCOME -- exhaustive, one row per actual ClipboardReadOutcome member (9 total,
    // Success excluded -- covered separately below)
    // ==================================================================

    // NOTE: ClipboardEvaluationDisposition is internal, and a public [Theory] method cannot take a
    // less-accessible parameter type (CS0051) -- expectedTerminal (bool, a public-compatible type)
    // stands in for it here; ClassifyRead_EveryDefinedNonSuccessMemberIsCovered below cross-checks
    // this table's row COUNT against the real enum's actual member count.
    [Theory]
    [InlineData(ClipboardReadOutcome.NotRunning, false)]
    [InlineData(ClipboardReadOutcome.Busy, false)]
    [InlineData(ClipboardReadOutcome.FormatUnavailable, true)]
    [InlineData(ClipboardReadOutcome.NativeFailure, false)]
    [InlineData(ClipboardReadOutcome.MalformedData, true)]
    [InlineData(ClipboardReadOutcome.InvalidExpectedTarget, false)]
    [InlineData(ClipboardReadOutcome.TargetUnavailable, false)]
    [InlineData(ClipboardReadOutcome.TargetChanged, false)]
    public void ClassifyRead_MatchesFrozenTable(ClipboardReadOutcome outcome, bool expectTerminal)
    {
        var expected = expectTerminal ? ClipboardEvaluationDisposition.Terminal : ClipboardEvaluationDisposition.Retryable;
        Assert.Equal(expected, ClipboardEvaluationOutcomeClassifier.ClassifyRead(outcome));
    }

    [Fact]
    public void ClassifyRead_EveryDefinedNonSuccessMemberIsCovered()
    {
        var covered = new[]
        {
            ClipboardReadOutcome.NotRunning, ClipboardReadOutcome.Busy, ClipboardReadOutcome.FormatUnavailable,
            ClipboardReadOutcome.NativeFailure, ClipboardReadOutcome.MalformedData,
            ClipboardReadOutcome.InvalidExpectedTarget, ClipboardReadOutcome.TargetUnavailable,
            ClipboardReadOutcome.TargetChanged,
        };
        var allNonSuccess = Enum.GetValues<ClipboardReadOutcome>().Where(v => v != ClipboardReadOutcome.Success);

        Assert.Equal(allNonSuccess.OrderBy(v => v), covered.OrderBy(v => v));
    }

    [Fact]
    public void ClassifyRead_FormatUnavailable_IsTerminal_NotRetryableDefault()
    {
        Assert.Equal(ClipboardEvaluationDisposition.Terminal, ClipboardEvaluationOutcomeClassifier.ClassifyRead(ClipboardReadOutcome.FormatUnavailable));
    }

    [Fact]
    public void ClassifyRead_MalformedData_IsTerminal()
    {
        Assert.Equal(ClipboardEvaluationDisposition.Terminal, ClipboardEvaluationOutcomeClassifier.ClassifyRead(ClipboardReadOutcome.MalformedData));
    }

    [Fact]
    public void ClassifyRead_Success_ThrowsContractViolation()
    {
        Assert.Throws<ArgumentException>(() => ClipboardEvaluationOutcomeClassifier.ClassifyRead(ClipboardReadOutcome.Success));
    }

    [Fact]
    public void ClassifyRead_UndefinedValue_ThrowsOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ClipboardEvaluationOutcomeClassifier.ClassifyRead((ClipboardReadOutcome)999));
    }

    // ==================================================================
    // WRITE OUTCOME -- exhaustive, one row per actual ClipboardWriteOutcome member (13 total,
    // Success excluded -- covered separately below)
    // ==================================================================

    [Theory]
    [InlineData(ClipboardWriteOutcome.NotRunning, false)]
    [InlineData(ClipboardWriteOutcome.InvalidExpectedSequence, false)]
    [InlineData(ClipboardWriteOutcome.InvalidText, true)]
    [InlineData(ClipboardWriteOutcome.Busy, false)]
    [InlineData(ClipboardWriteOutcome.SequenceChanged, false)]
    [InlineData(ClipboardWriteOutcome.NativeFailure, false)]
    [InlineData(ClipboardWriteOutcome.VerificationUnavailable, false)]
    [InlineData(ClipboardWriteOutcome.Superseded, false)]
    [InlineData(ClipboardWriteOutcome.ReadBackMismatch, false)]
    [InlineData(ClipboardWriteOutcome.InvalidExpectedTarget, false)]
    [InlineData(ClipboardWriteOutcome.TargetUnavailable, false)]
    [InlineData(ClipboardWriteOutcome.TargetChanged, false)]
    public void ClassifyWrite_MatchesFrozenTable(ClipboardWriteOutcome outcome, bool expectTerminal)
    {
        var expected = expectTerminal ? ClipboardEvaluationDisposition.Terminal : ClipboardEvaluationDisposition.Retryable;
        Assert.Equal(expected, ClipboardEvaluationOutcomeClassifier.ClassifyWrite(outcome));
    }

    [Fact]
    public void ClassifyWrite_EveryDefinedNonSuccessMemberIsCovered()
    {
        var covered = new[]
        {
            ClipboardWriteOutcome.NotRunning, ClipboardWriteOutcome.InvalidExpectedSequence,
            ClipboardWriteOutcome.InvalidText, ClipboardWriteOutcome.Busy, ClipboardWriteOutcome.SequenceChanged,
            ClipboardWriteOutcome.NativeFailure, ClipboardWriteOutcome.VerificationUnavailable,
            ClipboardWriteOutcome.Superseded, ClipboardWriteOutcome.ReadBackMismatch,
            ClipboardWriteOutcome.InvalidExpectedTarget, ClipboardWriteOutcome.TargetUnavailable,
            ClipboardWriteOutcome.TargetChanged,
        };
        var allNonSuccess = Enum.GetValues<ClipboardWriteOutcome>().Where(v => v != ClipboardWriteOutcome.Success);

        Assert.Equal(allNonSuccess.OrderBy(v => v), covered.OrderBy(v => v));
    }

    [Fact]
    public void ClassifyWrite_InvalidText_IsTerminal_TheOnlyTerminalWriteOutcome()
    {
        Assert.Equal(ClipboardEvaluationDisposition.Terminal, ClipboardEvaluationOutcomeClassifier.ClassifyWrite(ClipboardWriteOutcome.InvalidText));
    }

    [Fact]
    public void ClassifyWrite_Success_ThrowsContractViolation()
    {
        Assert.Throws<ArgumentException>(() => ClipboardEvaluationOutcomeClassifier.ClassifyWrite(ClipboardWriteOutcome.Success));
    }

    [Fact]
    public void ClassifyWrite_UndefinedValue_ThrowsOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ClipboardEvaluationOutcomeClassifier.ClassifyWrite((ClipboardWriteOutcome)999));
    }

    // ==================================================================
    // ENUM DEFAULT SAFETY
    // ==================================================================

    [Fact]
    public void ClipboardEvaluationDisposition_DefaultIsRetryable()
    {
        Assert.Equal(ClipboardEvaluationDisposition.Retryable, default);
    }

    [Fact]
    public void ClassifierType_IsInternal()
    {
        Assert.False(typeof(ClipboardEvaluationOutcomeClassifier).IsPublic);
    }
}
