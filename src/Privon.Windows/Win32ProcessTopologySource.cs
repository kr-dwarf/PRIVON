using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Privon.Windows;

/// <summary>
/// PRIVON 0.3.1 Gate 031F6F (E2) -- the only production implementation of
/// <see cref="IProcessTopologySource"/>. Owns its own, NEW P/Invoke declarations exclusively --
/// never widens the frozen nested native classes inside <see cref="Win32ForegroundTargetSource"/>.
///
/// PARENT_API (frozen): <see cref="TryGetParentProcessId"/> uses
/// NtQueryInformationProcess(ProcessBasicInformation).InheritedFromUniqueProcessId exclusively --
/// never a process-table snapshot/enumeration API of any kind, and never an ancestor walk.
///
/// SIGNATURE_REUSE (frozen): <see cref="ResolveSignature"/> reuses the existing, unmodified,
/// stateless <see cref="IExecutableSignatureVerifier"/>/<see cref="Win32ExecutableSignatureVerifier"/>
/// pair -- opening the executable FILE (never the process) exactly once via the same CreateFileW
/// technique <see cref="Win32ForegroundTargetSource"/> already established, verifying, then closing
/// the file handle. No TTL, no path cache, no retained-signer cache of any kind here (a future
/// caller -- <see cref="BrowserHostBindingResolver"/> -- is solely responsible for calling this at
/// most once per mechanical binding).
/// </summary>
internal sealed class Win32ProcessTopologySource : IProcessTopologySource
{
    // Minimum access right for read-only inspection -- never PROCESS_QUERY_INFORMATION (wider) and
    // never PROCESS_VM_READ.
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;
    private const uint RetentionAccess = ProcessQueryLimitedInformation | Synchronize;

    private const int ErrorInsufficientBuffer = 122;
    private const int AppModelErrorNoPackage = 15700;

    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001; // deliberately excludes FILE_SHARE_WRITE/FILE_SHARE_DELETE
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x80;
    private static readonly nint InvalidFileHandleValue = new(-1);

    private const int StatusSuccess = 0; // NTSTATUS STATUS_SUCCESS
    private const int ProcessBasicInformationClass = 0;

    private const uint WaitObject0 = 0x00000000;
    private const uint WaitTimeout = 0x00000102;
    private const uint WaitFailed = 0xFFFFFFFF;

    private readonly IExecutableSignatureVerifier _verifier = new Win32ExecutableSignatureVerifier();

    public bool TryOpenForInspection(uint processId, out SafeProcessHandle? handle) =>
        TryOpen(processId, ProcessQueryLimitedInformation, out handle);

    public bool TryOpenForRetention(uint processId, out SafeProcessHandle? handle) =>
        TryOpen(processId, RetentionAccess, out handle);

    private static bool TryOpen(uint processId, uint desiredAccess, out SafeProcessHandle? handle)
    {
        handle = null;
        if (processId == 0)
            return false;

        var opened = ProcessNativeMethods.OpenProcess(desiredAccess, false, processId);
        if (opened.IsInvalid)
        {
            opened.Dispose();
            return false;
        }

        handle = opened;
        return true;
    }

    public bool TryGetImagePath(SafeProcessHandle handle, out string? imagePath)
    {
        imagePath = null;

        uint capacity = 1024;
        var buffer = new StringBuilder((int)capacity);
        if (!ProcessNativeMethods.QueryFullProcessImageNameW(handle, 0, buffer, ref capacity))
        {
            capacity = 32768;
            buffer = new StringBuilder((int)capacity);
            if (!ProcessNativeMethods.QueryFullProcessImageNameW(handle, 0, buffer, ref capacity))
                return false;
        }

        string fullPath = buffer.ToString(0, (int)capacity);
        if (string.IsNullOrWhiteSpace(fullPath))
            return false;

        imagePath = fullPath;
        return true;
    }

    public bool TryGetCreationTimeTicks(SafeProcessHandle handle, out long creationTimeTicks)
    {
        creationTimeTicks = 0;
        if (!ProcessNativeMethods.GetProcessTimes(handle, out var creation, out _, out _, out _))
            return false;

        creationTimeTicks = ((long)creation.dwHighDateTime << 32) | creation.dwLowDateTime;
        return true;
    }

    public bool TryGetParentProcessId(SafeProcessHandle handle, out uint parentProcessId)
    {
        parentProcessId = 0;

        var info = new PROCESS_BASIC_INFORMATION();
        int size = Marshal.SizeOf<PROCESS_BASIC_INFORMATION>();
        int status = ProcessNativeMethods.NtQueryInformationProcess(
            handle, ProcessBasicInformationClass, ref info, size, out _);

        if (status != StatusSuccess)
            return false;

        parentProcessId = (uint)info.InheritedFromUniqueProcessId.ToInt64();
        return true;
    }

    public PackageIdentityResolution ResolvePackageIdentity(SafeProcessHandle handle, out string? packageFamilyName)
    {
        packageFamilyName = null;

        uint length = 0;
        int rc = ProcessNativeMethods.GetPackageFamilyName(handle, ref length, null);

        if (rc == AppModelErrorNoPackage)
            return PackageIdentityResolution.NoPackage;

        if (rc != ErrorInsufficientBuffer || length == 0)
            return PackageIdentityResolution.Unresolved;

        var buffer = new StringBuilder((int)length);
        rc = ProcessNativeMethods.GetPackageFamilyName(handle, ref length, buffer);
        if (rc == AppModelErrorNoPackage)
            return PackageIdentityResolution.NoPackage;
        if (rc != 0)
            return PackageIdentityResolution.Unresolved;

        packageFamilyName = buffer.ToString();
        return PackageIdentityResolution.Resolved;
    }

    public ExecutableSignatureResolution ResolveSignature(SafeProcessHandle handle, string imagePath, out string? signerOrganization)
    {
        signerOrganization = null;

        nint fileHandle = FileNativeMethods.CreateFileW(
            imagePath, GenericRead, FileShareRead, 0, OpenExisting, FileAttributeNormal, 0);
        if (fileHandle == InvalidFileHandleValue)
            return ExecutableSignatureResolution.Unresolved;

        try
        {
            return _verifier.Verify(fileHandle, imagePath, out signerOrganization);
        }
        finally
        {
            FileNativeMethods.CloseHandle(fileHandle);
        }
    }

    public RetainedProcessLiveness CheckLiveness(SafeProcessHandle handle)
    {
        if (handle.IsClosed || handle.IsInvalid)
            return RetainedProcessLiveness.Unavailable;

        uint result = ProcessNativeMethods.WaitForSingleObject(handle, 0);
        return result switch
        {
            WaitTimeout => RetainedProcessLiveness.Alive,
            WaitObject0 => RetainedProcessLiveness.Exited,
            _ => RetainedProcessLiveness.Unavailable, // WAIT_FAILED / unrecognized -- fail closed.
        };
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

    // PRIVON 0.3.1 Gate 031F6F (E2) -- deliberately a NEW, SEPARATE nested class, never merged into
    // Win32ForegroundTargetSource's own frozen nested native classes (section 8/36).
    private static class ProcessNativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern SafeProcessHandle OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryFullProcessImageNameW(SafeProcessHandle hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetProcessTimes(
            SafeProcessHandle hProcess, out FILETIME lpCreationTime, out FILETIME lpExitTime,
            out FILETIME lpKernelTime, out FILETIME lpUserTime);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int GetPackageFamilyName(SafeProcessHandle hProcess, ref uint packageFamilyNameLength, StringBuilder? packageFamilyName);

        [DllImport("ntdll.dll")]
        public static extern int NtQueryInformationProcess(
            SafeProcessHandle processHandle, int processInformationClass,
            ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(SafeProcessHandle hHandle, uint dwMilliseconds);
    }

    // PRIVON 0.3.1 Gate 031F6F (E2) -- the executable-file-handle surface used ONLY for signature
    // verification, deliberately a separate small P/Invoke duplication (section 36) rather than
    // reopening Win32ForegroundTargetSource's own frozen FileIdentityNativeMethods.
    private static class FileNativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern nint CreateFileW(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode, nint lpSecurityAttributes,
            uint dwCreationDisposition, uint dwFlagsAndAttributes, nint hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(nint hObject);
    }
}
