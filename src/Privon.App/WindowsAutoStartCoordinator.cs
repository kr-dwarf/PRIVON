namespace Privon.App;

/// <summary>
/// Phase 0.2I -- the auto-start POLICY layer: owns the literal Run-value name ("PRIVON") and the
/// exact expected command line (the current process's own executable path, quoted), and answers
/// exactly three questions -- <see cref="IsEnabled"/>/<see cref="TryEnable"/>/<see cref="TryDisable"/>
/// -- against an injected <see cref="IWindowsAutoStartRegistration"/> seam. This type, not
/// <see cref="Privon.Windows.WindowsAutoStartManager"/>, is where the 0.1/0.2 product decision "what
/// string means PRIVON's own auto-start entry" actually lives -- exactly mirroring how
/// <c>TargetGate</c> is the only place the literal "ChatGPT" comparison lives while
/// <c>Privon.Windows</c> stays generic.
///
/// DEFAULT_OFF (Phase 0.2I AUTO-START CONTRACT, frozen): this type never writes a registration on
/// its own -- <see cref="TryEnable"/> is only ever called in direct, synchronous response to an
/// explicit user action (the tray toggle click); merely constructing this type, or calling
/// <see cref="IsEnabled"/> to query current state, performs a READ-ONLY registry lookup and can
/// never itself create, modify, or remove any registration.
///
/// EXACT_PATH_MATCH (Phase 0.2I STALE/DIFFERENT PATH requirement): <see cref="IsEnabled"/> compares
/// the CURRENTLY-STORED value against the CURRENT process's own expected command line via ordinal
/// string equality -- a registration that exists but points at a different (stale/moved) executable
/// path is reported OFF, never a false-positive ON, so the UI never claims a stale registration is
/// still valid for the executable that is actually running right now. Re-enabling from that state
/// simply overwrites the stale value with the current path (<see cref="TryEnable"/> always writes
/// the CURRENT expected command line, never merges/preserves the old one).
///
/// FAIL_CLOSED_ON_UNRESOLVABLE_PATH: if this process's own executable path cannot be resolved (the
/// injected path provider returns null/empty -- see <see cref="_currentExecutablePathProvider"/>'s
/// own doc), <see cref="IsEnabled"/> reports <see langword="false"/> and <see cref="TryEnable"/>
/// reports failure without ever calling the registration seam at all -- this type never writes a
/// malformed/unquoted/garbage command line.
/// </summary>
internal sealed class WindowsAutoStartCoordinator
{
    /// <summary>The one literal Run-value name this whole codebase ever uses for its own
    /// registration -- see this type's own class doc for why this literal lives here and not in
    /// <see cref="Privon.Windows.WindowsAutoStartManager"/>.</summary>
    internal const string ValueName = "PRIVON";

    private readonly IWindowsAutoStartRegistration _registration;
    private readonly Func<string?> _currentExecutablePathProvider;

    public WindowsAutoStartCoordinator(IWindowsAutoStartRegistration registration)
        : this(registration, () => Environment.ProcessPath)
    {
    }

    /// <summary>Test-only: overrides how this type resolves "the current executable's own path" --
    /// production always uses <see cref="Environment.ProcessPath"/> (the real running process's
    /// own main module path), never a hardcoded/build-configuration-specific path, so a Debug build
    /// can never accidentally register a Debug path as a Release auto-start entry or vice versa.
    /// </summary>
    internal WindowsAutoStartCoordinator(IWindowsAutoStartRegistration registration, Func<string?> currentExecutablePathProvider)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(currentExecutablePathProvider);
        _registration = registration;
        _currentExecutablePathProvider = currentExecutablePathProvider;
    }

    /// <summary>True only when a PRIVON registration currently exists AND its stored command line
    /// is EXACTLY the current executable's own expected (quoted) command line.</summary>
    public bool IsEnabled()
    {
        var expected = BuildExpectedCommandLine();
        if (expected is null) return false;

        return _registration.TryGetValue(ValueName, out var actual)
            && string.Equals(actual, expected, StringComparison.Ordinal);
    }

    /// <summary>Writes the current executable's own quoted command line as the PRIVON Run value.
    /// Returns <see langword="false"/> -- never throws -- on any failure (unresolvable path, or a
    /// registry write failure already isolated inside the registration seam).</summary>
    public bool TryEnable()
    {
        var expected = BuildExpectedCommandLine();
        if (expected is null) return false;

        return _registration.TrySetValue(ValueName, expected);
    }

    /// <summary>Removes ONLY the PRIVON Run value. Returns <see langword="false"/> -- never throws
    /// -- on failure; an already-absent registration is a successful outcome (see
    /// <see cref="Privon.Windows.WindowsAutoStartManager.TryDeleteValue"/>'s own idempotency doc).
    /// </summary>
    public bool TryDisable() => _registration.TryDeleteValue(ValueName);

    // PATH_QUOTING (Phase 0.2I EXECUTABLE PATH SAFETY): unconditionally wraps the resolved path in
    // double quotes -- safe and correct whether or not the path itself contains spaces, and matches
    // the Windows Run-value convention exactly (e.g. "C:\Program Files\PRIVON\PRIVON.exe").
    private string? BuildExpectedCommandLine()
    {
        var path = _currentExecutablePathProvider();
        return string.IsNullOrEmpty(path) ? null : $"\"{path}\"";
    }
}
