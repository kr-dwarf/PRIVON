using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Privon.Windows;

/// <summary>
/// Phase 3A.5 STEP2 -- the only production implementation of <see cref="IForegroundTargetSource"/>.
/// P/Invoke declarations scoped strictly to foreground-window/process identity (GetForegroundWindow/
/// GetWindowThreadProcessId only) plus a BCL process-name-by-PID lookup -- no third-party package,
/// no UI Automation, no window class/title inspection (PRODUCTION_TARGET_PID_STRATEGY/CHATGPT
/// PROCESS NAME POLICY: mechanical facts only, this file never compares against "ChatGPT" or any
/// other name).
///
/// PRODUCTION_TARGET_PID_STRATEGY (resolved): deliberately does NOT enumerate or cache any PID set
/// at startup or between calls -- every <see cref="TryGetProcessName"/> call resolves the CURRENT
/// process identity for the CURRENT foreground PID, fresh, every time. This avoids the staleness
/// Phase 0's own startup-cached-PID-set approach was explicitly evidence-only for (process restart/
/// exit/PID-reuse over a long-running app's lifetime).
/// </summary>
internal sealed class Win32ForegroundTargetSource : IForegroundTargetSource
{
    public nint GetForegroundWindow() => NativeMethods.GetForegroundWindow();

    public bool TryGetWindowThreadProcessId(nint hwnd, out uint processId)
    {
        processId = 0;
        uint threadId = NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        if (threadId == 0 || pid == 0)
            return false;

        processId = pid;
        return true;
    }

    // PROCESS_EXIT_RACE: Process.GetProcessById(pid) throws ArgumentException when no process
    // with that ID exists on the system (already exited, or the PID was never valid) --
    // InvalidOperationException can also surface if the process exits between GetProcessById
    // succeeding and the ProcessName property access completing. Both are ordinary, expected
    // races for a foreground-window snapshot (the window/process a caller is inspecting can
    // legitimately disappear between steps) -- neither is allowed to escape as an exception here;
    // both resolve to a plain `false`, exactly like every other unresolved case in this seam.
    public bool TryGetProcessName(uint processId, out string? processName)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            processName = process.ProcessName;
            return true;
        }
        catch (ArgumentException)
        {
            processName = null;
            return false;
        }
        catch (InvalidOperationException)
        {
            processName = null;
            return false;
        }
        catch (Win32Exception)
        {
            processName = null;
            return false;
        }
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern nint GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
    }
}
