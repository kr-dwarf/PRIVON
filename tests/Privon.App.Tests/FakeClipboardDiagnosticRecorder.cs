using Privon.App;

namespace Privon.App.Tests;

// Deterministic, OS-free double for IClipboardDiagnosticRecorder -- lets ClipboardPrivacyCoordinator's
// Phase 3C STEP41 diagnostic observation points be exercised and asserted on without any real file
// I/O. No mocking framework -- hand-written, matching every other Fake* in this project.
internal sealed class FakeClipboardDiagnosticRecorder : IClipboardDiagnosticRecorder
{
    private readonly object _gate = new();
    private readonly List<ClipboardDiagnosticEvent> _events = [];

    /// <summary>Snapshot, thread-safe -- the coordinator's own worker/owner threads may call
    /// <see cref="Record"/> concurrently with a test thread reading this.</summary>
    public IReadOnlyList<ClipboardDiagnosticEvent> Events
    {
        get
        {
            lock (_gate) return _events.ToArray();
        }
    }

    /// <summary>When set, <see cref="Record"/> throws instead of recording -- lets a test prove
    /// SINK_FAILURE_ISOLATION: a diagnostic recorder that always fails must never affect the
    /// coordinator's own functional behavior.</summary>
    public bool ThrowOnRecord { get; set; }

    public void Record(ClipboardDiagnosticEvent diagnosticEvent)
    {
        if (ThrowOnRecord)
            throw new InvalidOperationException("Synthetic diagnostic recorder failure for test.");

        lock (_gate)
        {
            _events.Add(diagnosticEvent);
        }
    }
}
