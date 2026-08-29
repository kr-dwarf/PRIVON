using Privon.Windows;

namespace Privon.App;

/// <summary>
/// Phase 3B STEP2 -- the only production implementation of <see cref="IForegroundTargetCapture"/>.
/// A single-line delegation to an owned <see cref="ForegroundTargetInspector"/> -- no policy, no
/// duplicated foreground-inspection logic. Constructed by the live composition root -- see
/// <see cref="PrivonAppComposition.BuildGraph"/>.
/// </summary>
internal sealed class ForegroundTargetCapture : IForegroundTargetCapture
{
    private readonly ForegroundTargetInspector _inspector;

    public ForegroundTargetCapture() : this(new ForegroundTargetInspector())
    {
    }

    internal ForegroundTargetCapture(ForegroundTargetInspector inspector)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        _inspector = inspector;
    }

    public ForegroundTargetSnapshot Capture() => _inspector.Capture();
}
