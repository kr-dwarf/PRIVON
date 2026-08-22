namespace Privon.Core;

/// <summary>
/// Tracks the current (RevisionId, SnapshotHash) pair for a composition. Any change in the
/// observed text advances the revision; callers can also force an advance for
/// non-text-content events (window switch, undo/redo) that must still invalidate prior
/// validations even if the text momentarily returns to identical content. This class never
/// stores the raw text it is given.
/// </summary>
public sealed class RevisionTracker
{
    private long _counter;

    public RevisionId CurrentRevisionId { get; private set; } = RevisionId.None;
    public SnapshotHash CurrentHash { get; private set; } = SnapshotHash.Empty;
    public RevisionStamp CurrentStamp => new(CurrentRevisionId, CurrentHash);

    public RevisionStamp Observe(ReadOnlySpan<char> currentText)
    {
        var hash = SnapshotHash.From(currentText);
        if (hash != CurrentHash)
        {
            Advance();
            CurrentHash = hash;
        }
        return CurrentStamp;
    }

    /// <summary>Bumps the revision without regard to text content.</summary>
    public RevisionStamp ForceAdvance()
    {
        Advance();
        return CurrentStamp;
    }

    private void Advance()
    {
        _counter++;
        CurrentRevisionId = new RevisionId(_counter);
    }
}
