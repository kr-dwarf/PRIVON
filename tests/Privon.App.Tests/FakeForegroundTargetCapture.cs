using Privon.App;
using Privon.Windows;

namespace Privon.App.Tests;

// Deterministic, OS-free double for IForegroundTargetCapture.
internal sealed class FakeForegroundTargetCapture : IForegroundTargetCapture
{
    public ForegroundTargetSnapshot SnapshotToReturn { get; set; } =
        new(IsResolved: true, ProcessId: 4242, ProcessName: "ChatGPT");

    public int CaptureCallCount { get; private set; }
    public List<string> CallLog { get; } = [];

    public ForegroundTargetSnapshot Capture()
    {
        CallLog.Add(nameof(Capture));
        CaptureCallCount++;
        return SnapshotToReturn;
    }
}
