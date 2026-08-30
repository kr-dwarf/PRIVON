using Privon.Windows;

namespace Privon.App;

/// <summary>
/// Phase 3B STEP2 -- the only production implementation of <see cref="IForegroundTargetCapture"/>.
/// A single-line delegation to an owned <see cref="ForegroundTargetInspector"/> -- no policy, no
/// duplicated foreground-inspection logic. Constructed by the live composition root -- see
/// <see cref="PrivonAppComposition.BuildGraph"/>.
/// </summary>
internal sealed class ForegroundTargetCapture : IForegroundTargetCapture, IDisposable
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

    /// <summary>PRIVON 0.3.0 Gate 1C.1 -- propagates disposal to the owned
    /// <see cref="ForegroundTargetInspector"/>, so <see cref="PrivonAppComposition"/>'s own shutdown
    /// reaches the real <c>Win32ForegroundTargetSource</c>'s retained process/file handles.</summary>
    public void Dispose() => _inspector.Dispose();
}
