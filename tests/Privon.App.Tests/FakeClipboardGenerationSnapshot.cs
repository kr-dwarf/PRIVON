using Privon.App;

namespace Privon.App.Tests;

// Deterministic, OS-free double for IClipboardGenerationSnapshot -- lets
// ClipboardComposerVerifier's generation-freshness checks be exercised without depending on the
// real lock-based ClipboardDecisionScopeLifecycle. No mocking framework -- hand-written, matching
// every other Fake* in this project.
internal sealed class FakeClipboardGenerationSnapshot : IClipboardGenerationSnapshot
{
    public long Generation { get; set; }

    /// <summary>
    /// When set below <see cref="int.MaxValue"/>, <see cref="CurrentGeneration"/> returns
    /// <see cref="Generation"/> for the first <see cref="ChangeAfterReads"/> reads, then
    /// <see cref="ChangedGeneration"/> for every read after that -- lets a test deterministically
    /// simulate a clipboard notification advancing the generation between two specific
    /// <see cref="CurrentGeneration"/> reads inside one fully-synchronous method (e.g.
    /// <c>ClipboardComposerVerifier.Publish</c>'s PRECHECK vs. POST_INSTALL_VALIDATION reads),
    /// without needing real concurrency.
    /// </summary>
    public int ChangeAfterReads { get; set; } = int.MaxValue;
    public long ChangedGeneration { get; set; }

    private int _readCount;

    public int ReadCount => _readCount;

    public long CurrentGeneration
    {
        get
        {
            _readCount++;
            return _readCount > ChangeAfterReads ? ChangedGeneration : Generation;
        }
    }
}
