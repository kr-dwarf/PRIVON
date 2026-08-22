namespace Privon.App;

/// <summary>
/// Phase 3C STEP30.1/STEP32 -- the narrow, read-only generation-observability seam a future
/// composer-verification component needs, split out from <see cref="IClipboardDecisionScopeLifecycle"/>
/// specifically so that component never needs the wider Runtime-Decision-scope-facing surface
/// (<see cref="IClipboardDecisionScopeLifecycle.TryPublish"/>/<see cref="IClipboardDecisionScopeLifecycle.Reset"/>/
/// <see cref="IClipboardDecisionScopeLifecycle.GetActiveScope"/>/<see cref="IClipboardDecisionScopeLifecycle.IsActive"/>),
/// which it has no legitimate reason to touch -- exactly the same narrow-seam-over-a-richer-concrete-type
/// pattern already used throughout this project (<see cref="IClipboardNotificationLifecycle"/> vs
/// <see cref="IClipboardDecisionScopeLifecycle"/> on the SAME concrete
/// <see cref="ClipboardDecisionScopeLifecycle"/> object being the direct precedent this interface
/// extends, as a third narrow view on that one object -- <see cref="ClipboardDecisionScopeLifecycle"/>
/// itself is NOT broadened in any other way to accommodate this).
/// </summary>
internal interface IClipboardGenerationSnapshot
{
    /// <summary>The current clipboard-attempt generation. Safe metadata -- see
    /// <c>Privon.Core.RevisionId</c>'s own "just a counter" precedent.</summary>
    long CurrentGeneration { get; }
}
