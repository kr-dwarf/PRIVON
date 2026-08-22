namespace Privon.App;

/// <summary>
/// Phase 0.2D (STEP61) -- which trigger produced one <see cref="ClipboardCoordinatorWorkItem"/>.
/// Declared with <see cref="None"/> first so <c>default(ClipboardCoordinatorTriggerKind)</c> is
/// never mistaken for a real trigger -- the same discipline used throughout this codebase (e.g.
/// <c>ClipboardReadOutcome.NotRunning</c>, <c>ClipboardEvaluationState.NotEvaluated</c>).
/// </summary>
internal enum ClipboardCoordinatorTriggerKind
{
    None = 0,
    ClipboardChanged,
    ForegroundChanged,
}

/// <summary>
/// Phase 0.2D (STEP61) -- the shared mailbox element type replacing the clipboard-only
/// <c>ClipboardDispatchItem</c> (retained, unused by production, only because a FROZEN Phase 0.2C
/// test -- <c>ClipboardDecisionScopeLifecycleTests.LifecycleTypes_AreNotPublic</c> -- still asserts
/// its accessibility; see this STEP's own report for why that file could not be touched). Carries
/// exactly what each of the two trigger kinds actually has available at arrival time:
///
/// <see cref="ClipboardCoordinatorTriggerKind.ClipboardChanged"/> carries the exact generation
/// <c>IClipboardNotificationLifecycle.AdvanceOnClipboardNotification</c> returned for it, captured
/// immutably at arrival (the same WHY_READING_LATER_IS_NOT_ENOUGH reasoning
/// <c>ClipboardDispatchItem</c>'s own doc already established -- a worker that only reads "the
/// current generation" later cannot tell whether it is still the one that applies).
///
/// <see cref="ClipboardCoordinatorTriggerKind.ForegroundChanged"/> deliberately carries NO
/// meaningful generation -- a foreground-focus change is not itself a clipboard-content change, so
/// it has no generation of its own to capture. <see cref="ClipboardGeneration"/> is simply left at
/// its default (<c>0</c>) for this kind; the coordinator's own pre-pipeline intake step reads
/// <c>IClipboardGenerationSnapshot.CurrentGeneration</c> FRESH, at the moment it actually needs a
/// generation to attempt an evaluation claim with (see <see cref="ClipboardPrivacyCoordinator"/>'s
/// own TARGET_TOKEN_FLOW / cross-trigger doc), never from this field.
///
/// No raw clipboard text of any kind lives here -- unlike <c>ClipboardDispatchItem</c>, this type
/// does not even carry the Windows <c>ClipboardChangeNotification</c> itself (nothing downstream of
/// the mailbox ever needed the notification's own fields beyond what has already been captured as
/// <see cref="ClipboardGeneration"/>). <c>default(ClipboardCoordinatorWorkItem)</c> has
/// <see cref="Kind"/> == <see cref="ClipboardCoordinatorTriggerKind.None"/> and must never be
/// dispatched into the coordinator's privacy pipeline -- both factory methods below are the only
/// ways to construct a real, Kind-bearing instance.
/// </summary>
internal readonly record struct ClipboardCoordinatorWorkItem
{
    public ClipboardCoordinatorTriggerKind Kind { get; }
    public long ClipboardGeneration { get; }

    private ClipboardCoordinatorWorkItem(ClipboardCoordinatorTriggerKind kind, long clipboardGeneration)
    {
        Kind = kind;
        ClipboardGeneration = clipboardGeneration;
    }

    public static ClipboardCoordinatorWorkItem ForClipboardChange(long generation) =>
        new(ClipboardCoordinatorTriggerKind.ClipboardChanged, generation);

    public static ClipboardCoordinatorWorkItem ForForegroundChange() =>
        new(ClipboardCoordinatorTriggerKind.ForegroundChanged, clipboardGeneration: 0);
}
