using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Privon.Windows;

/// <summary>
/// Phase 3A.5 STEP2 -- the only production implementation of <see cref="IForegroundTargetSource"/>.
/// P/Invoke declarations scoped strictly to foreground-window/process identity -- no third-party
/// package, no UI Automation, no window class/title inspection (PRODUCTION_TARGET_PID_STRATEGY/
/// CHATGPT PROCESS NAME POLICY: mechanical facts only, this file never compares against "ChatGPT",
/// any package family name, or any other product identity).
///
/// PRODUCTION_TARGET_PID_STRATEGY (resolved): deliberately does NOT enumerate or cache any PID set
/// at startup or between calls -- every resolution answers for the CURRENT foreground PID, fresh,
/// every time. This avoids the staleness Phase 0's own startup-cached-PID-set approach was
/// explicitly evidence-only for (process restart/exit/PID-reuse over a long-running app's lifetime).
///
/// BUG-004 Gate 2H.3 (E+): identity resolution no longer uses <c>System.Diagnostics.Process</c> at
/// all. See <see cref="TryResolveConfirmedForegroundIdentity"/> for the single-handle contract and
/// why the previous two-independent-lookups approach was unsound.
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

    // Minimum access right that satisfies BOTH QueryFullProcessImageNameW and GetPackageFamilyName.
    // Deliberately NOT PROCESS_QUERY_INFORMATION (wider) and never PROCESS_VM_READ.
    private const uint ProcessQueryLimitedInformation = 0x1000;

    // Win32 status codes GetPackageFamilyName can return, named rather than left as magic numbers.
    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;

    /// <summary>
    /// SINGLE_HANDLE_IDENTITY (Gate 2H.3, E+): opens ONE native process handle FIRST, derives BOTH
    /// identity facts from that same handle, and -- while it is still open -- re-confirms the current
    /// foreground process is still that pinned process. Every failure is an ordinary, expected
    /// condition (the process exited, the handle could not be opened, the foreground moved on) and
    /// resolves to a plain <see langword="false"/>; nothing here throws for an ordinary race.
    ///
    /// PROCESS_NAME_SOURCE: <c>QueryFullProcessImageNameW</c> on the pinned handle. Only the
    /// BASENAME (extension stripped) is kept, matching this seam's long-standing process-name
    /// semantics. PATH_IS_NEVER_IDENTITY: the full image path is a transient local only -- never
    /// returned, never stored, never logged, and never any part of an authorization decision.
    ///
    /// FOREGROUND_CONFIRMATION: the second <c>GetForegroundWindow</c>/<c>GetWindowThreadProcessId</c>
    /// pair below runs BEFORE the handle is released, which is what makes the PID comparison
    /// meaningful -- the pinned process object (and therefore its identifier) cannot have been
    /// replaced while this handle is held. The window handle itself is deliberately not compared:
    /// a different window belonging to the same pinned process is a valid match.
    /// </summary>
    public bool TryResolveConfirmedForegroundIdentity(
        uint expectedProcessId,
        out string? processName,
        out PackageIdentityResolution packageIdentity,
        out string? packageFamilyName)
    {
        processName = null;
        packageIdentity = PackageIdentityResolution.Unresolved;
        packageFamilyName = null;

        if (expectedProcessId == 0)
            return false;

        nint handle = ProcessIdentityNativeMethods.OpenProcess(ProcessQueryLimitedInformation, false, expectedProcessId);
        if (handle == 0)
            return false;

        try
        {
            if (!TryGetProcessNameFromHandle(handle, out string? resolvedName))
                return false;

            var resolvedIdentity = ResolvePackageIdentityFromHandle(handle, out string? resolvedPfn);

            // FOREGROUND_CONFIRMATION -- still inside the handle's scope, deliberately.
            nint confirmHwnd = NativeMethods.GetForegroundWindow();
            if (confirmHwnd == 0)
                return false;
            if (!TryGetWindowThreadProcessId(confirmHwnd, out uint confirmPid))
                return false;
            if (confirmPid != expectedProcessId)
                return false;

            processName = resolvedName;
            packageIdentity = resolvedIdentity;
            packageFamilyName = resolvedPfn;
            return true;
        }
        finally
        {
            ProcessIdentityNativeMethods.CloseHandle(handle);
        }
    }

    private static bool TryGetProcessNameFromHandle(nint handle, out string? processName)
    {
        processName = null;

        // MAX_PATH is not sufficient for long paths; 32767 is the documented upper bound.
        uint capacity = 1024;
        var buffer = new StringBuilder((int)capacity);
        if (!ProcessIdentityNativeMethods.QueryFullProcessImageNameW(handle, 0, buffer, ref capacity))
        {
            capacity = 32768;
            buffer = new StringBuilder((int)capacity);
            if (!ProcessIdentityNativeMethods.QueryFullProcessImageNameW(handle, 0, buffer, ref capacity))
                return false;
        }

        // PATH_IS_NEVER_IDENTITY: fullPath exists only long enough to take its basename.
        string fullPath = buffer.ToString(0, (int)capacity);
        if (string.IsNullOrWhiteSpace(fullPath))
            return false;

        string baseName = Path.GetFileNameWithoutExtension(fullPath);
        if (string.IsNullOrWhiteSpace(baseName))
            return false;

        processName = baseName;
        return true;
    }

    private static PackageIdentityResolution ResolvePackageIdentityFromHandle(nint handle, out string? packageFamilyName)
    {
        packageFamilyName = null;

        uint length = 0;
        int rc = PackageNativeMethods.GetPackageFamilyName(handle, ref length, null);

        if (rc == AppModelErrorNoPackage)
        {
            // Definitive, successful fact: this process genuinely has no package identity -- never
            // conflated with an inspection failure (see PackageIdentityResolution's own doc).
            return PackageIdentityResolution.NoPackage;
        }

        if (rc != ErrorInsufficientBuffer || length == 0)
            return PackageIdentityResolution.Unresolved;

        var buffer = new StringBuilder((int)length);
        rc = PackageNativeMethods.GetPackageFamilyName(handle, ref length, buffer);
        if (rc == AppModelErrorNoPackage)
            return PackageIdentityResolution.NoPackage;
        if (rc != ErrorSuccess)
            return PackageIdentityResolution.Unresolved;

        packageFamilyName = buffer.ToString();
        return PackageIdentityResolution.Resolved;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern nint GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
    }

    // BUG-004 Gate 2F -- deliberately a SEPARATE nested class from NativeMethods above (never
    // merged into it) so the existing, frozen structural regression
    // (Win32Source_NativeMethods_DeclaresOnlyForegroundWindowAndThreadProcessId, which asserts
    // NativeMethods declares EXACTLY {GetForegroundWindow, GetWindowThreadProcessId} and nothing
    // else) needs no modification at all.
    private static class PackageNativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int GetPackageFamilyName(nint hProcess, ref uint packageFamilyNameLength, StringBuilder? packageFamilyName);
    }

    // BUG-004 Gate 2H.3 -- process-handle lifetime and image-name primitives, kept in their own
    // nested class for the same reason as PackageNativeMethods above. Unicode-explicit
    // (QueryFullProcessImageNameW). No window-text/title/URL/clipboard API appears here, and none
    // ever may -- locked down by this capability's own structural regression.
    private static class ProcessIdentityNativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(nint hObject);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryFullProcessImageNameW(nint hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);
    }
}
