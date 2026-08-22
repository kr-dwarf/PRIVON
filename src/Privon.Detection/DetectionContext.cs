namespace Privon.Detection;

/// <summary>
/// Carrier passed to each detector. Intentionally minimal for Phase 2A -- just the
/// normalized view detectors scan against. Context-boost inputs (surrounding PII signals,
/// combination-risk scoring) are added in a later phase once more than one detector exists.
/// </summary>
public sealed class DetectionContext
{
    public NormalizedView View { get; }

    public DetectionContext(NormalizedView view)
    {
        View = view ?? throw new ArgumentNullException(nameof(view));
    }
}
