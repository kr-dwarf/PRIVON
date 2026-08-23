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
/// <see cref="Icon"/> uses <see cref="SystemIcons.Application"/> -- no custom <c>.ico</c> resource
/// is embedded for 0.1, keeping packaging minimal; swapping in a branded icon later does not
/// change this type's contract.
///
/// Phase 0.2I adds exactly one more menu item -- a checkable "Windows 시작 시 자동 실행" toggle,
/// placed above a separator from the pre-existing Exit item -- and nothing else; still no
/// dashboard, no settings window.
/// </summary>
internal sealed class WinFormsTrayIconSurface : ITrayIconSurface
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _autoStartItem;
    private readonly ToolStripMenuItem _exitItem;
    private bool _disposed;

    public WinFormsTrayIconSurface()
    {
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
        _menu.Items.Add(_autoStartItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_exitItem);

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "PRIVON — 실행 중",
            ContextMenuStrip = _menu,
            Visible = false,
        };
    }

    public event EventHandler? ExitRequested;
    public event EventHandler? AutoStartToggleRequested;

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
        _menu.Dispose();
        _autoStartItem.Dispose();
        _exitItem.Dispose();
    }
}
