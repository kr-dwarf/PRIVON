using Privon.Windows;

namespace Privon.Windows.IntegrationTests;

// Deterministic, OS-free double for IComposerTextSource -- lets ComposerTextReader's identity/
// CHECK1-CHECK2/failure-mapping logic be exercised without any real ChatGPT window, UI Automation
// tree, or COM object of any kind. Impossible to fake a real AutomationElement (no usable public
// constructor) -- IComposerTextSource is deliberately shaped so it never needs to. Defaults to a
// snapshot that matches ComposerTextReaderTests' own DefaultExpectedTarget (PID 4242, class
// containing "ProseMirror-focused", ControlType Edit) with an empty but successfully-read text, so
// a test only needs to override the specific field it cares about. No mocking framework.
internal sealed class FakeComposerTextSource : IComposerTextSource
{
    public ComposerElementSnapshot SnapshotToReturn { get; set; } =
        ComposerElementSnapshot.Resolved(processId: 4242, className: "ProseMirror ProseMirror-focused", isEditControlType: true, text: "");

    public int QueryCallCount { get; private set; }
    public List<string> CallLog { get; } = [];

    /// <summary>When set, <see cref="QueryFocusedElement"/> throws this instead of returning --
    /// lets a test prove ComposerTextReader's own outer WORKER_SURVIVAL catch handles a truly
    /// unexpected exception type that Win32ComposerTextSource's narrower catches would not.</summary>
    public Exception? ThrowOnQuery { get; set; }

    public ComposerElementSnapshot QueryFocusedElement()
    {
        CallLog.Add(nameof(QueryFocusedElement));
        QueryCallCount++;

        if (ThrowOnQuery is { } ex)
            throw ex;

        return SnapshotToReturn;
    }
}
