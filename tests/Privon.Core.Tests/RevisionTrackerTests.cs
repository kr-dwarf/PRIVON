using Privon.Core;

namespace Privon.Core.Tests;

public class RevisionTrackerTests
{
    // Test 1: Revision 변경 시 이전 validation 무효 (explicit revision bump, e.g. window
    // switch/undo, even without necessarily changing the observed text hash).
    [Fact]
    public void ForceAdvance_InvalidatesPreviousStamp()
    {
        var tracker = new RevisionTracker();
        var before = tracker.Observe("synthetic-text-A");

        var after = tracker.ForceAdvance();

        Assert.False(before.Matches(after));
        Assert.NotEqual(before.RevisionId, after.RevisionId);
    }

    // Test 2: snapshot hash 변경 시 (즉, 텍스트가 바뀌면) 이전 validation 무효.
    [Fact]
    public void ObservingDifferentText_InvalidatesPreviousStamp()
    {
        var tracker = new RevisionTracker();
        var before = tracker.Observe("synthetic-text-A");

        var after = tracker.Observe("synthetic-text-B");

        Assert.False(before.Matches(after));
        Assert.NotEqual(before.Hash, after.Hash);
        Assert.NotEqual(before.RevisionId, after.RevisionId);
    }

    [Fact]
    public void ObservingSameTextTwice_DoesNotAdvanceRevision()
    {
        var tracker = new RevisionTracker();
        var first = tracker.Observe("synthetic-text-A");
        var second = tracker.Observe("synthetic-text-A");

        Assert.True(first.Matches(second));
        Assert.Equal(first.RevisionId, second.RevisionId);
    }

    [Fact]
    public void SnapshotHash_NeverExposesOriginalText()
    {
        var hash = SnapshotHash.From("synthetic-text-A");
        Assert.DoesNotContain("synthetic-text-A", hash.ToString());
    }
}
