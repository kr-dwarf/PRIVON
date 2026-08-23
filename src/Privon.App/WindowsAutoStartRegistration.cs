using Privon.Windows;

namespace Privon.App;

/// <summary>
/// Phase 0.2I -- the only production implementation of <see cref="IWindowsAutoStartRegistration"/>.
/// A three-line delegation to an owned <see cref="WindowsAutoStartManager"/> -- no policy, no
/// duplicated registry logic (mirrors <see cref="ForegroundTargetCapture"/>'s own single-delegation
/// shape exactly).
/// </summary>
internal sealed class WindowsAutoStartRegistration : IWindowsAutoStartRegistration
{
    private readonly WindowsAutoStartManager _manager;

    public WindowsAutoStartRegistration() : this(new WindowsAutoStartManager())
    {
    }

    internal WindowsAutoStartRegistration(WindowsAutoStartManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        _manager = manager;
    }

    public bool TryGetValue(string valueName, out string? commandLine) => _manager.TryGetValue(valueName, out commandLine);

    public bool TrySetValue(string valueName, string commandLine) => _manager.TrySetValue(valueName, commandLine);

    public bool TryDeleteValue(string valueName) => _manager.TryDeleteValue(valueName);
}
