using System.Runtime.InteropServices;
using System.Text;

namespace Privon.WebLaunchMeasurement;

// Gate E5B.1 -- dev-tool-local, independent raw OS process-ancestry capture.
//
// Deliberately NOT a reuse of Privon.Windows's own internal Win32ProcessTopologySource /
// IProcessTopologySource: those types are `internal` to Privon.Windows, and this project
// intentionally never requests InternalsVisibleTo (see Privon.WebLaunchMeasurement.csproj's own doc
// comment) or widens their accessibility. This is instead a small, standalone duplication of the
// same well-known, already-established-in-this-repo Win32 techniques (NtQueryInformationProcess's
// ProcessBasicInformation.InheritedFromUniqueProcessId for the parent PID, QueryFullProcessImageNameW
// for the image path, GetProcessTimes for a raw, comparable creation timestamp) -- used ONLY to
// record the ancestry chain's raw mechanical facts (pid / process name / image path / creation time)
// as independent evidence.
//
// This is a SEPARATE fact source from BrowserHostBindingResolver, which Program.cs also calls, and
// whose own topology/package/signature JUDGMENT is retained unchanged in the evidence file. Neither
// source is derived from the other: the immediate-parent and grandparent PIDs recorded here come
// exclusively from this type's own NtQueryInformationProcess calls, never from
// BrowserHostBinding.Topology or any other resolver output.
//
// Captures pid / process name / image path / creation time only. Never a command line, environment
// variable, window title, user document, browser URL, or profile data of any kind.
internal static class ProcessChainCapture
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const uint DesiredAccess = ProcessQueryLimitedInformation | Synchronize;

    private const uint WaitObject0 = 0x00000000;

    private const int ErrorAccessDenied = 5;

    private const int StatusSuccess = 0; // NTSTATUS STATUS_SUCCESS
    private const int ProcessBasicInformationClass = 0;

    /// <summary>Opens <paramref name="pid"/> with the minimum rights needed for a one-shot mechanical
    /// read (PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, the latter solely to distinguish
    /// "still alive" from "already exited" via a zero-timeout wait), captures its facts, and closes
    /// the handle immediately -- this tool never retains a process handle.</summary>
    public static ProcessHopRecord CaptureHop(uint pid)
    {
        if (pid == 0)
            return ProcessHopRecord.NotApplicable();

        nint handle = NativeMethods.OpenProcess(DesiredAccess, false, pid);
        if (handle == 0)
        {
            int error = Marshal.GetLastWin32Error();
            return error == ErrorAccessDenied
                ? ProcessHopRecord.AccessDenied(pid)
                : ProcessHopRecord.Unavailable(pid);
        }

        try
        {
            string? imagePath = TryGetImagePath(handle);
            string? processName = imagePath is null ? null : Path.GetFileNameWithoutExtension(imagePath);
            long? creationTicks = TryGetCreationTimeTicks(handle);
            uint? parentPid = TryGetParentProcessId(handle);
            bool exited = NativeMethods.WaitForSingleObject(handle, 0) == WaitObject0;

            var status = exited ? ProcessHopStatus.Exited : ProcessHopStatus.Resolved;
            return new ProcessHopRecord(status, pid, processName, imagePath, creationTicks, parentPid);
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static string? TryGetImagePath(nint handle)
    {
        uint capacity = 1024;
        var buffer = new StringBuilder((int)capacity);
        if (!NativeMethods.QueryFullProcessImageNameW(handle, 0, buffer, ref capacity))
        {
            capacity = 32768;
            buffer = new StringBuilder((int)capacity);
            if (!NativeMethods.QueryFullProcessImageNameW(handle, 0, buffer, ref capacity))
                return null;
        }

        string path = buffer.ToString(0, (int)capacity);
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private static long? TryGetCreationTimeTicks(nint handle)
    {
        if (!NativeMethods.GetProcessTimes(handle, out var creation, out _, out _, out _))
            return null;

        return ((long)creation.dwHighDateTime << 32) | creation.dwLowDateTime;
    }

    private static uint? TryGetParentProcessId(nint handle)
    {
        var info = new PROCESS_BASIC_INFORMATION();
        int size = Marshal.SizeOf<PROCESS_BASIC_INFORMATION>();
        int status = NativeMethods.NtQueryInformationProcess(handle, ProcessBasicInformationClass, ref info, size, out _);
        if (status != StatusSuccess)
            return null;

        return (uint)info.InheritedFromUniqueProcessId.ToInt64();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(nint hObject);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryFullProcessImageNameW(nint hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetProcessTimes(nint hProcess, out FILETIME lpCreationTime, out FILETIME lpExitTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

        [DllImport("ntdll.dll")]
        public static extern int NtQueryInformationProcess(nint processHandle, int processInformationClass, ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);
    }
}

/// <summary>The narrow, mechanical, non-crashing outcome of <see cref="ProcessChainCapture.CaptureHop"/>
/// for one ancestry position. Never a guess -- a hop this tool could not resolve is recorded as
/// exactly that, never silently filled in from <c>BrowserHostBinding.Topology</c> or any other
/// derived source.</summary>
internal enum ProcessHopStatus
{
    /// <summary>There is no PID to inspect at this position (e.g. the parent PID resolved to 0, or
    /// the prior hop in the chain could not itself be opened, so its own parent PID is unknown).</summary>
    NotApplicable,

    /// <summary>OpenProcess failed for a reason other than access denial (e.g. the PID no longer
    /// exists / was already reused).</summary>
    Unavailable,

    /// <summary>OpenProcess failed specifically with ERROR_ACCESS_DENIED.</summary>
    AccessDenied,

    /// <summary>The process was opened successfully, but a zero-timeout WaitForSingleObject on the
    /// same handle immediately reported it already signaled (exited). Facts captured before this
    /// check (image path / creation time / parent PID) are still recorded if they themselves
    /// succeeded.</summary>
    Exited,

    /// <summary>The process was opened successfully and was alive at inspection time.</summary>
    Resolved,
}

/// <summary>One raw, independently-observed ancestry position (host, immediate parent, or
/// grandparent). <see cref="Pid"/> is populated whenever the PID itself is known, even if opening it
/// failed; <see cref="ProcessName"/>/<see cref="ImagePath"/>/<see cref="CreationTimeRawTicks"/> are
/// populated only when their own underlying Win32 call succeeded.</summary>
internal sealed record ProcessHopRecord(
    ProcessHopStatus Status,
    uint? Pid,
    string? ProcessName,
    string? ImagePath,
    long? CreationTimeRawTicks,
    uint? ParentPid)
{
    public static ProcessHopRecord NotApplicable() =>
        new(ProcessHopStatus.NotApplicable, null, null, null, null, null);

    public static ProcessHopRecord Unavailable(uint pid) =>
        new(ProcessHopStatus.Unavailable, pid, null, null, null, null);

    public static ProcessHopRecord AccessDenied(uint pid) =>
        new(ProcessHopStatus.AccessDenied, pid, null, null, null, null);

    /// <summary>ISO-8601 UTC rendering of <see cref="CreationTimeRawTicks"/> (a raw Win32 FILETIME),
    /// for human readability only -- ordering comparisons must use the raw ticks, never this string.</summary>
    public string? CreationTimeUtc =>
        CreationTimeRawTicks is long ticks ? DateTime.FromFileTimeUtc(ticks).ToString("O") : null;
}
