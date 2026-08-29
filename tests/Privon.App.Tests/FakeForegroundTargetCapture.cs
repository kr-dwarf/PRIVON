using Privon.App;
using Privon.Windows;

namespace Privon.App.Tests;

// Deterministic, OS-free double for IForegroundTargetCapture.
//
// BUG-004 Gate 2G: this type's DEFAULT SnapshotToReturn has always meant, and continues to mean,
// "the currently-supported ChatGPT target, so pipeline processing proceeds past TargetGate" -- the
// entire reason it was ever "ChatGPT" rather than an arbitrary name. Widening it to also carry the
// approved package identity is therefore the ONE legitimate default-widening case the Gate 2G
// migration instructions describe ("a central helper that already semantically means
// CreateSupportedChatGptTarget() may be widened once") -- not a case of silently hiding the new
// policy inside a fake used ambiguously for both supported and unsupported scenarios. Every test
// that wants an UNSUPPORTED target already, and still must, override this property explicitly with
// its own distinct snapshot (a different process name, IsResolved: false, etc.) -- none of those
// negative fixtures are affected by this widening.
internal sealed class FakeForegroundTargetCapture : IForegroundTargetCapture
{
    public ForegroundTargetSnapshot SnapshotToReturn { get; set; } =
        new(IsResolved: true, ProcessId: 4242, ProcessName: "ChatGPT",
            PackageIdentity: PackageIdentityResolution.Resolved, PackageFamilyName: "OpenAI.Codex_2p2nqsd0c76g0");

    public int CaptureCallCount { get; private set; }
    public List<string> CallLog { get; } = [];

    public ForegroundTargetSnapshot Capture()
    {
        CallLog.Add(nameof(Capture));
        CaptureCallCount++;
        return SnapshotToReturn;
    }
}
