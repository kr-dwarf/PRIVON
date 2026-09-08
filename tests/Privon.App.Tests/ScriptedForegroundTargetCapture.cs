using Privon.App;
using Privon.Windows;

namespace Privon.App.Tests;

// PRIVON 0.3.1 Gate 031F4.2 -- TEST-ONLY deterministic double for IForegroundTargetCapture, for the
// Phase-C2 freshness RED suite. Distinct from FakeForegroundTargetCapture (whose default snapshot
// deliberately means "the supported Windows target, so the coordinator pipeline proceeds") -- this
// one carries NO default product meaning at all and adds the two seams the C2 contract needs:
//
//   ExceptionToThrow -- proves the EXCEPTION_OWNER split (Gate 031F4.1): the App freshness
//     implementation contains no catch-all, so a throwing capture must propagate out of
//     IsStillCurrent(); the already-GREEN Privon.Windows boundary is the only thing that catches.
//
//   OnCapture -- a synchronous callback invoked INSIDE Capture(), before the snapshot is returned.
//     This is what makes the FRESHNESS_EPOCH_BRACKET testable with zero timing dependency: a test
//     raises one foreground-change event from inside the capture, so the epoch provably advances
//     between the algorithm's epochBefore read (step 4) and its epochAfter read (step 14) with no
//     sleep, no Task.Delay, and no real thread race.
internal sealed class ScriptedForegroundTargetCapture : IForegroundTargetCapture
{
    public ForegroundTargetSnapshot SnapshotToReturn { get; set; }

    /// <summary>When set, Capture() throws this instead of returning -- see this type's own doc.</summary>
    public Exception? ExceptionToThrow { get; set; }

    /// <summary>Invoked synchronously inside Capture(), before the snapshot is returned (and before
    /// ExceptionToThrow is thrown, when both are set).</summary>
    public Action? OnCapture { get; set; }

    public int CaptureCallCount { get; private set; }
    public List<string> CallLog { get; } = [];

    public ForegroundTargetSnapshot Capture()
    {
        CallLog.Add(nameof(Capture));
        CaptureCallCount++;

        OnCapture?.Invoke();

        if (ExceptionToThrow is not null)
            throw ExceptionToThrow;

        return SnapshotToReturn;
    }
}
