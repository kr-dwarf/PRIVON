using Microsoft.Win32;

namespace Privon.Windows;

/// <summary>
/// Phase 0.2I -- the mechanical HKCU Run-key adapter. Deliberately generic: takes a caller-supplied
/// <c>valueName</c>/<c>commandLine</c> on every call and never hardcodes "PRIVON" anywhere in this
/// type (WINDOWS_VS_APP_RESPONSIBILITY, matching the existing <c>ForegroundTargetInspector</c>/
/// <c>Win32ForegroundTargetSource</c> precedent exactly -- Windows knows mechanical OS facts only,
/// the App layer owns which literal name/command line those facts mean anything for). The registry
/// KEY PATH itself (<c>Software\Microsoft\Windows\CurrentVersion\Run</c>) is a Windows OS fact, not
/// a PRIVON product-policy decision -- exactly like <c>WM_CLIPBOARDUPDATE</c>/
/// <c>EVENT_SYSTEM_FOREGROUND</c> are OS facts this codebase already hardcodes directly in this
/// project -- so it is not injected in production, only overridable via the internal
/// test-only constructor below (a real, disposable scratch subkey elsewhere under HKCU, never the
/// real Run key, so automated tests can never leave a stray real auto-start entry behind).
///
/// CURRENT_USER_ONLY / NO_ADMIN (Phase 0.2I AUTO-START CONTRACT, frozen): every operation targets
/// <see cref="Registry.CurrentUser"/> exclusively -- HKLM/Task Scheduler/Startup-folder file
/// creation are never used anywhere in this type, and <see cref="Registry.CurrentUser"/> never
/// requires elevation for any subkey a normal user process already owns.
///
/// FAIL_CLOSED (matching this codebase's established "ordinary expected failures resolve to a
/// typed <see langword="false"/>, never an escaping exception" convention -- e.g.
/// <c>Win32ForegroundTargetSource.TryResolveConfirmedForegroundIdentity</c>): registry access can legitimately fail for
/// reasons entirely outside this type's control (permission changes, corrupted hive, concurrent
/// external deletion, ...) -- every method below catches broadly and returns <see langword="false"/>
/// rather than letting an exception escape into caller code that never expects one from a UI-adjacent
/// settings toggle.
/// </summary>
public sealed class WindowsAutoStartManager
{
    private const string DefaultRunSubKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly string _subKeyPath;

    public WindowsAutoStartManager() : this(DefaultRunSubKeyPath)
    {
    }

    /// <summary>Test-only: overrides the target subkey path so automated tests can exercise real
    /// HKCU registry read/write/delete behavior against a disposable scratch subkey, never the real
    /// Run key.</summary>
    internal WindowsAutoStartManager(string subKeyPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(subKeyPath);
        _subKeyPath = subKeyPath;
    }

    /// <summary>True only when a string value named <paramref name="valueName"/> currently exists
    /// under this adapter's subkey -- <paramref name="commandLine"/> is that exact stored string,
    /// unmodified (no trim/normalize/re-quote of any kind).</summary>
    public bool TryGetValue(string valueName, out string? commandLine)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_subKeyPath, writable: false);
            var raw = key?.GetValue(valueName);
            commandLine = raw as string;
            return commandLine is not null;
        }
        catch
        {
            commandLine = null;
            return false;
        }
    }

    /// <summary>Writes (creating or overwriting) <paramref name="commandLine"/> as a REG_SZ value
    /// named <paramref name="valueName"/>. Creates the subkey itself if it does not already exist
    /// (the real Run key always already exists on any real Windows installation, but this stays
    /// defensive for the test-only scratch-subkey path above).</summary>
    public bool TrySetValue(string valueName, string commandLine)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(_subKeyPath, writable: true);
            if (key is null) return false;
            key.SetValue(valueName, commandLine, RegistryValueKind.String);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Removes ONLY the value named <paramref name="valueName"/> -- every other value
    /// under this subkey (including any other application's own Run entry) is never read, compared,
    /// or touched in any way. Idempotent: a subkey that does not exist at all, or a value that is
    /// already absent, is a successful "already off" outcome (<see langword="true"/>), never a
    /// failure -- matching DISABLE's own "OFF means no registration exists" contract.</summary>
    public bool TryDeleteValue(string valueName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_subKeyPath, writable: true);
            if (key is null) return true;
            key.DeleteValue(valueName, throwOnMissingValue: false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
