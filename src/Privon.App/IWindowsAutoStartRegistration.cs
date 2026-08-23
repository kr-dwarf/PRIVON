namespace Privon.App;

/// <summary>
/// Phase 0.2I -- narrow App-owned seam over <see cref="Privon.Windows.WindowsAutoStartManager"/>,
/// scoped to exactly what <see cref="WindowsAutoStartCoordinator"/> needs (matches the established
/// <see cref="IForegroundTargetCapture"/>/<see cref="IClipboardReadTransport"/> pattern exactly).
/// Exists purely so the App-level auto-start POLICY (value name, expected command line, quoting --
/// see <see cref="WindowsAutoStartCoordinator"/>'s own doc) can be tested with a hand-written fake
/// instead of real HKCU registry access. Mirrors the Windows-layer type's own method shapes 1:1 --
/// no policy of any kind lives here or in the production wrapper that implements it.
/// </summary>
internal interface IWindowsAutoStartRegistration
{
    bool TryGetValue(string valueName, out string? commandLine);
    bool TrySetValue(string valueName, string commandLine);
    bool TryDeleteValue(string valueName);
}
