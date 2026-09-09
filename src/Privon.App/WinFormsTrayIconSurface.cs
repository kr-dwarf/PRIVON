using System.Drawing;
using System.Windows.Forms;

namespace Privon.App;

/// <summary>
/// Phase 3C STEP40 -- the only production implementation of <see cref="ITrayIconSurface"/>.
///
/// TRAY_MECHANISM (Phase 3C STEP39.1's deliberately-left-open implementation choice, resolved
/// here): <see cref="System.Windows.Forms.NotifyIcon"/>, via the SDK's built-in
/// <c>UseWindowsForms</c> framework reference -- no NuGet package, no new project, no new project
/// reference (see <c>Privon.App.csproj</c>). Chosen over a hand-rolled <c>Shell_NotifyIcon</c>
/// P/Invoke implementation because it is dramatically smaller and less risky for a pure UI-chrome
/// concern: a raw implementation would need its own message-only-window class registration,
/// <c>WM_TASKBARCREATED</c>/custom-message handling, icon-handle lifetime management, and
/// <c>TrackPopupMenu</c> plumbing for even a single-item context menu -- none of which is a
/// security-sensitive OS-integration boundary the way clipboard/foreground-target/session-lock
/// P/Invoke is elsewhere in this codebase (which remains hand-rolled for exactly that reason).
/// <see cref="System.Windows.Forms.Application"/> is never referenced anywhere in this file --
/// only <see cref="NotifyIcon"/>/<see cref="ContextMenuStrip"/>/<see cref="ToolStripMenuItem"/>
/// are used, so no ambiguity with <see cref="System.Windows.Application"/> (WPF) arises; this
/// file's own <c>using</c> list intentionally does not include <c>System.Windows</c>.
///
/// No separate <c>System.Windows.Forms.Application.Run()</c> message loop is started -- the
/// underlying native window <see cref="NotifyIcon"/> creates internally shares the same OS message
/// queue as whatever thread constructs it, which in production is WPF's own main STA thread
/// (already pumped by <see cref="System.Windows.Threading.Dispatcher"/>) -- a common, working
/// technique for hosting a WinForms tray icon inside a WPF application.
///
/// PRIVON 0.3.2 Gate 032-B1: <see cref="Icon"/> now uses the official PRIVON PV icon, loaded via
/// <see cref="BrandResources.LoadTrayIcon"/> from this assembly's own embedded
/// <c>Assets\privon.ico</c> resource -- never <see cref="SystemIcons.Application"/>, never a
/// filesystem path, never a temp-extracted file. That call constructs a real <see cref="Icon"/>
/// instance owning an unmanaged GDI handle distinct from the shared system icon it replaces, so
/// this type now owns and disposes it (<see cref="_brandIcon"/>) exactly like every other
/// disposable field below.
///
/// Phase 0.2I adds exactly one more menu item -- a checkable "Windows 시작 시 자동 실행" toggle,
/// placed above a separator from the pre-existing Exit item -- and nothing else; still no
/// dashboard, no settings window.
///
/// PRIVON v0.2.1 Gate 3C adds exactly one more menu item -- "Settings..." -- placed above the same
/// separator, alongside the auto-start toggle, so the destructive Exit command stays visually
/// isolated below its own separator. No new tray icon, no new process/launcher -- opening Settings
/// routes through the SAME <see cref="PrivonAppUiBridge"/>-owned dispatcher hand-off as every other
/// tray command here.
/// </summary>
internal sealed class WinFormsTrayIconSurface : ITrayIconSurface
{
    private readonly Icon _brandIcon;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _settingsItem;
    private readonly ToolStripMenuItem _autoStartItem;
    private readonly ToolStripMenuItem _exitItem;
    private bool _disposed;

    public WinFormsTrayIconSurface()
    {
        _settingsItem = new ToolStripMenuItem("Settings...");
        _settingsItem.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);

        // CheckOnClick is deliberately false -- the coordinator, not this menu item itself, decides
        // the final checked state AFTER a real enable/disable attempt actually succeeds or fails
        // (see ITrayIconSurface.SetAutoStartChecked's own doc). If CheckOnClick were true, WinForms
        // would flip the visual checkbox the instant the user clicks, before any registry attempt
        // ever ran -- exactly the "표시가 거짓으로 성공을 주장" failure mode the 0.2I contract
        // forbids.
        _autoStartItem = new ToolStripMenuItem("Windows 시작 시 자동 실행") { CheckOnClick = false };
        _autoStartItem.Click += (_, _) => AutoStartToggleRequested?.Invoke(this, EventArgs.Empty);

        _exitItem = new ToolStripMenuItem("Exit");
        _exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        _menu = new ContextMenuStrip();
        _menu.Items.Add(_settingsItem);
        _menu.Items.Add(_autoStartItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_exitItem);

        _brandIcon = BrandResources.LoadTrayIcon();

        _icon = new NotifyIcon
        {
            Icon = _brandIcon,
            Text = "PRIVON — 실행 중",
            ContextMenuStrip = _menu,
            Visible = false,
        };
    }

    public event EventHandler? ExitRequested;
    public event EventHandler? AutoStartToggleRequested;
    public event EventHandler? SettingsRequested;

    public void Show()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _icon.Visible = true;
    }

    public void SetAutoStartChecked(bool isChecked)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _autoStartItem.Checked = isChecked;
    }

    /// <summary>Deterministic disposal -- sets <c>Visible = false</c> BEFORE disposing so the icon
    /// is removed from the shell rather than left as a stale "ghost" entry (a well-known
    /// <see cref="NotifyIcon"/> pitfall when a process exits without doing this). Idempotent; safe
    /// to call even if <see cref="Show"/> was never called.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _icon.Visible = false;
        _icon.Dispose();
        _brandIcon.Dispose();
        _menu.Dispose();
        _settingsItem.Dispose();
        _autoStartItem.Dispose();
        _exitItem.Dispose();
    }
}
