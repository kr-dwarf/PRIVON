using Privon.App;
using Privon.Windows;

namespace Privon.App.Tests;

// PRIVON 0.3.1 Gate 031F5C -- deterministic, OS/Native-Messaging-free double for the real
// IWebClipboardAuthorizationSource. No mocking framework -- hand-written, matching every other
// fake in this project. Supports every Phase-D behavioral scenario without Task.Delay, sleep, or
// polling: a scripted per-call result queue, a throwing mode, and a pending-completion mode backed
// by a caller-controlled TaskCompletionSource.
internal sealed class FakeWebClipboardAuthorizationSource : IWebClipboardAuthorizationSource
{
    private readonly Queue<WebClipboardAuthorization?> _scriptedResults = new();

    public int CallCount { get; private set; }
    public List<ForegroundTargetSnapshot> ReceivedSnapshots { get; } = [];

    /// <summary>Result returned when the scripted queue is empty and <see cref="PendingResult"/> is
    /// unset -- defaults to null (ordinary decline), matching every other fake's fail-closed default
    /// in this project.</summary>
    public WebClipboardAuthorization? NextResult { get; set; }

    /// <summary>When set, thrown by AuthorizeAsync instead of returning -- proves the router/
    /// coordinator/resolver never convert an unexpected source defect into an ordinary decline.</summary>
    public Exception? ThrowOnAuthorize { get; set; }

    /// <summary>
    /// When set, AuthorizeAsync returns THIS TaskCompletionSource's own Task instead of an
    /// already-completed one -- lets a test hold an authorization "in flight" deterministically and
    /// complete it explicitly later (Gate 031F5C ASYNC_MUST_BE_REAL / D-R16), with zero timing
    /// dependency.
    /// </summary>
    public TaskCompletionSource<WebClipboardAuthorization?>? PendingResult { get; set; }

    public void ScriptResults(params WebClipboardAuthorization?[] results)
    {
        foreach (var result in results)
            _scriptedResults.Enqueue(result);
    }

    public Task<WebClipboardAuthorization?> AuthorizeAsync(ForegroundTargetSnapshot foreground)
    {
        ReceivedSnapshots.Add(foreground);
        CallCount++;

        if (ThrowOnAuthorize is { } ex)
            throw ex;

        if (PendingResult is { } pending)
            return pending.Task;

        return Task.FromResult(_scriptedResults.TryDequeue(out var scripted) ? scripted : NextResult);
    }
}
