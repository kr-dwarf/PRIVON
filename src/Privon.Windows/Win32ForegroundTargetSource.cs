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
///
/// PRIVON 0.3.0 Gate 1B -- PROCESS_BOUND_REUSE: this instance additionally owns ONE retained
/// signer-evidence entry (<see cref="_retained"/>), never a dictionary/TTL/static cache (see
/// <see cref="IExecutableSignatureVerifier"/>'s own REUSE_OWNERSHIP doc). Real Authenticode
/// verification is measured at ~277ms median / ~331ms p95 -- far too slow to repeat on every
/// guarded check -- so once a Claude-shaped (<see cref="PackageIdentityResolution.NoPackage"/>)
/// process is verified <see cref="ExecutableSignatureResolution.Trusted"/>, that result is retained
/// bound to FOUR facts: the exact kernel process object (compared via <c>CompareObjectHandles</c>,
/// never PID), the retained process not having terminated (<c>GetExitCodeProcess</c>), the process
/// creation time as a corroborating fact, and the exact executable FILE object identity (volume
/// serial + file index, re-derived fresh on every check and compared against the identity captured
/// at verification time). PID equality, path equality, and process-name equality are each
/// individually and jointly insufficient to reuse -- see <see cref="TryReuseRetainedEvidence"/>.
/// A reuse MISS for the current observation unconditionally invalidates (and closes) any existing
/// retained entry BEFORE a fresh verification is attempted -- there is room for exactly one current
/// entry. Only a successful <see cref="ExecutableSignatureResolution.Trusted"/> result may ever
/// create a new retained entry (Untrusted/Unresolved/failed verification never does).
/// </summary>
internal sealed class Win32ForegroundTargetSource : IForegroundTargetSource, IDisposable
{
    private readonly IExecutableSignatureVerifier _verifier;
    private readonly object _reuseGate = new();
    private RetainedSignerEvidence? _retained;

    public Win32ForegroundTargetSource() : this(new Win32ExecutableSignatureVerifier())
    {
    }

    internal Win32ForegroundTargetSource(IExecutableSignatureVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        _verifier = verifier;
    }

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

    // PRIVON 0.3.0 Gate 1B -- process-bound signer-evidence reuse constants.
    private const uint StillActiveExitCode = 259; // STILL_ACTIVE
    private const uint DuplicateSameAccess = 0x00000002; // DUPLICATE_SAME_ACCESS
    private static readonly nint CurrentProcessPseudoHandle = new(-1); // GetCurrentProcess() pseudo-handle, well-known constant
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001; // deliberately excludes FILE_SHARE_WRITE/FILE_SHARE_DELETE
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x80;
    private static readonly nint InvalidFileHandleValue = new(-1);

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
        out string? packageFamilyName,
        out ExecutableSignatureResolution executableSignature,
        out string? signerOrganization)
    {
        processName = null;
        packageIdentity = PackageIdentityResolution.Unresolved;
        packageFamilyName = null;
        executableSignature = ExecutableSignatureResolution.NotInspected;
        signerOrganization = null;

        if (expectedProcessId == 0)
            return false;

        nint handle = ProcessIdentityNativeMethods.OpenProcess(ProcessQueryLimitedInformation, false, expectedProcessId);
        if (handle == 0)
            return false;

        try
        {
            if (!TryGetProcessNameFromHandle(handle, out string? resolvedName, out string? fullImagePath))
                return false;

            var resolvedIdentity = ResolvePackageIdentityFromHandle(handle, out string? resolvedPfn);

            // SIGNATURE_INSPECTION_GATE: only ever attempted for a DEFINITIVELY unpackaged process
            // -- see ExecutableSignatureResolution's own NotInspected doc. Performed here, inside
            // the pinned-handle scope, alongside package-identity resolution and BEFORE the
            // foreground re-confirmation below, per this type's own FOREGROUND LINEARIZATION
            // contract (Gate 1B): the re-confirmation must remain the LAST gate, unmoved.
            var resolvedSignature = ExecutableSignatureResolution.NotInspected;
            string? resolvedSignerOrganization = null;
            if (resolvedIdentity == PackageIdentityResolution.NoPackage && fullImagePath is not null)
            {
                resolvedSignature = ResolveExecutableSignature(handle, fullImagePath, out resolvedSignerOrganization);
            }

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
            executableSignature = resolvedSignature;
            signerOrganization = resolvedSignerOrganization;
            return true;
        }
        finally
        {
            ProcessIdentityNativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>
    /// PRIVON 0.3.0 Gate 1B -- the process-bound reuse decision, isolated from
    /// <see cref="TryResolveConfirmedForegroundIdentity"/>'s own foreground-window dependency so it
    /// can be exercised directly (real process handle, real file, injected
    /// <see cref="IExecutableSignatureVerifier"/> spy) without needing to control the real OS
    /// foreground window -- the correct owner-level test surface for CALL_COUNT/reuse/invalidation
    /// behavior. <paramref name="processHandle"/> MUST be an already-open, still-valid handle to the
    /// process currently being resolved (the same one <see cref="TryResolveConfirmedForegroundIdentity"/>
    /// already pinned); this method never opens or closes it.
    /// </summary>
    internal ExecutableSignatureResolution ResolveExecutableSignature(
        nint processHandle, string imagePath, out string? signerOrganization)
    {
        lock (_reuseGate)
        {
            if (TryReuseRetainedEvidence(processHandle, imagePath, out signerOrganization))
                return ExecutableSignatureResolution.Trusted;

            // Any existing retained entry is no longer current the instant reuse misses for THIS
            // observation -- there is room for exactly one entry, never a dictionary.
            InvalidateRetainedEvidence();

            // EXACT_FILE_OBJECT_CONTINUITY (Gate 1C.1, frozen): opened exactly ONCE here. This SAME
            // handle flows into the verifier (WinVerifyTrust binds to it directly, and any signer
            // certificate extraction reads the SAME live trust state -- never a path reopen) and --
            // only on a Trusted result -- into RetainedSignerEvidence. PATH IS NO LONGER IDENTITY
            // once this handle exists: imagePath is passed to Verify only because WINTRUST_FILE_INFO
            // itself requires it alongside hFile, never as a second source of truth.
            nint fileHandle = FileIdentityNativeMethods.CreateFileW(
                imagePath, GenericRead, FileShareRead, 0, OpenExisting, FileAttributeNormal, 0);
            if (fileHandle == InvalidFileHandleValue)
            {
                signerOrganization = null;
                return ExecutableSignatureResolution.Unresolved;
            }

            bool transferred = false;
            try
            {
                var result = _verifier.Verify(fileHandle, imagePath, out signerOrganization);
                if (result == ExecutableSignatureResolution.Trusted && !string.IsNullOrEmpty(signerOrganization))
                {
                    // On success, TryRetainEvidence takes ownership of fileHandle (bound into
                    // RetainedSignerEvidence, the SAME object WinVerifyTrust just verified) -- it is
                    // NOT closed below in that case. On failure to retain (e.g. the process-side
                    // duplicate/creation-time capture failed), this observation's own Trusted+org
                    // answer is still truthful and returned, but no reuse is possible next time, and
                    // fileHandle is closed below since ownership was never transferred.
                    transferred = TryRetainEvidence(processHandle, fileHandle, signerOrganization);
                }
                else
                {
                    // Untrusted/Unresolved/failed verification MUST NOT create a positive reusable
                    // entry -- no negative caching either.
                    signerOrganization = null;
                }

                return result;
            }
            finally
            {
                if (!transferred)
                    FileIdentityNativeMethods.CloseHandle(fileHandle);
            }
        }
    }

    private bool TryReuseRetainedEvidence(nint processHandle, string imagePath, out string? signerOrganization)
    {
        signerOrganization = null;
        var retained = _retained;
        if (retained is null)
            return false;

        // CASE 3/6: same kernel process object -- PID is never consulted here at all.
        if (!ReuseNativeMethods.CompareObjectHandles(processHandle, retained.ProcessHandle))
            return false;

        // CASE 4: the retained process must still be alive -- GetExitCodeProcess failing at all is
        // treated as "cannot prove still alive" -> no reuse; STILL_ACTIVE is the only exit code
        // that means "still alive".
        if (!ReuseNativeMethods.GetExitCodeProcess(retained.ProcessHandle, out uint exitCode) || exitCode != StillActiveExitCode)
            return false;

        // Corroborating fact: process creation time must still match the instance verified earlier.
        if (!ReuseNativeMethods.GetProcessTimes(processHandle, out var creation, out _, out _, out _))
            return false;
        if (FileTimeToTicks(creation) != retained.ProcessCreationTimeTicks)
            return false;

        // CASE 5/6/7/8: the CURRENT executable file's identity, re-derived fresh, must match the
        // identity captured when the retained evidence was verified.
        if (!TryGetFileIdentity(imagePath, out uint volumeSerial, out uint indexHigh, out uint indexLow))
            return false;
        if (volumeSerial != retained.VolumeSerialNumber || indexHigh != retained.FileIndexHigh || indexLow != retained.FileIndexLow)
            return false;

        // CASE 9: the retained verified executable handle itself must still be coherent.
        if (!TryGetFileIdentityFromHandle(retained.FileHandle, out uint retainedVolumeSerialNow, out uint retainedIndexHighNow, out uint retainedIndexLowNow))
            return false;
        if (retainedVolumeSerialNow != retained.VolumeSerialNumber || retainedIndexHighNow != retained.FileIndexHigh || retainedIndexLowNow != retained.FileIndexLow)
            return false;

        signerOrganization = retained.SignerOrganization;
        return true;
    }

    /// <summary>
    /// PRIVON 0.3.0 Gate 1C.1 -- <paramref name="fileHandle"/> is the EXACT, already-open handle
    /// <see cref="ResolveExecutableSignature"/> already passed to the verifier (the same object
    /// WinVerifyTrust evaluated) -- this method never reopens the executable by path. Returns
    /// <see langword="true"/> only if ownership of <paramref name="fileHandle"/> was successfully
    /// transferred into a new <see cref="RetainedSignerEvidence"/> (the caller must then NOT close
    /// it); <see langword="false"/> means retention failed for an unrelated reason (process handle
    /// could not be duplicated, or process times could not be read) and the caller still owns --
    /// and must still close -- <paramref name="fileHandle"/>.
    /// </summary>
    private bool TryRetainEvidence(nint processHandle, nint fileHandle, string signerOrganization)
    {
        if (!TryGetFileIdentityFromHandle(fileHandle, out uint volumeSerial, out uint indexHigh, out uint indexLow))
            return false;

        if (!ReuseNativeMethods.DuplicateHandle(
                CurrentProcessPseudoHandle, processHandle, CurrentProcessPseudoHandle,
                out nint duplicatedProcessHandle, 0, false, DuplicateSameAccess))
        {
            return false; // Cannot retain -- the next observation simply re-verifies; not a correctness gap.
        }

        if (!ReuseNativeMethods.GetProcessTimes(processHandle, out var creation, out _, out _, out _))
        {
            ReuseNativeMethods.CloseHandle(duplicatedProcessHandle);
            return false;
        }

        _retained = new RetainedSignerEvidence(
            duplicatedProcessHandle, fileHandle, volumeSerial, indexHigh, indexLow,
            FileTimeToTicks(creation), signerOrganization);
        return true;
    }

    private void InvalidateRetainedEvidence()
    {
        _retained?.Dispose();
        _retained = null;
    }

    private static bool TryGetFileIdentity(string imagePath, out uint volumeSerial, out uint indexHigh, out uint indexLow)
    {
        volumeSerial = 0;
        indexHigh = 0;
        indexLow = 0;

        nint handle = FileIdentityNativeMethods.CreateFileW(
            imagePath, GenericRead, FileShareRead, 0, OpenExisting, FileAttributeNormal, 0);
        if (handle == InvalidFileHandleValue)
            return false;

        try
        {
            return TryGetFileIdentityFromHandle(handle, out volumeSerial, out indexHigh, out indexLow);
        }
        finally
        {
            FileIdentityNativeMethods.CloseHandle(handle);
        }
    }

    private static bool TryGetFileIdentityFromHandle(nint handle, out uint volumeSerial, out uint indexHigh, out uint indexLow)
    {
        volumeSerial = 0;
        indexHigh = 0;
        indexLow = 0;

        if (!ReuseNativeMethods.GetFileInformationByHandle(handle, out var info))
            return false;

        volumeSerial = info.dwVolumeSerialNumber;
        indexHigh = info.nFileIndexHigh;
        indexLow = info.nFileIndexLow;
        return true;
    }

    private static long FileTimeToTicks(FILETIME ft) => ((long)ft.dwHighDateTime << 32) | ft.dwLowDateTime;

    public void Dispose()
    {
        lock (_reuseGate)
        {
            InvalidateRetainedEvidence();
        }
    }

    private sealed class RetainedSignerEvidence : IDisposable
    {
        public RetainedSignerEvidence(
            nint processHandle, nint fileHandle, uint volumeSerialNumber, uint fileIndexHigh,
            uint fileIndexLow, long processCreationTimeTicks, string signerOrganization)
        {
            ProcessHandle = processHandle;
            FileHandle = fileHandle;
            VolumeSerialNumber = volumeSerialNumber;
            FileIndexHigh = fileIndexHigh;
            FileIndexLow = fileIndexLow;
            ProcessCreationTimeTicks = processCreationTimeTicks;
            SignerOrganization = signerOrganization;
        }

        public nint ProcessHandle { get; }
        public nint FileHandle { get; }
        public uint VolumeSerialNumber { get; }
        public uint FileIndexHigh { get; }
        public uint FileIndexLow { get; }
        public long ProcessCreationTimeTicks { get; }
        public string SignerOrganization { get; }

        public void Dispose()
        {
            if (ProcessHandle != 0)
                ReuseNativeMethods.CloseHandle(ProcessHandle);
            if (FileHandle != 0)
                FileIdentityNativeMethods.CloseHandle(FileHandle);
        }
    }

    private static bool TryGetProcessNameFromHandle(nint handle, out string? processName, out string? fullImagePath)
    {
        processName = null;
        fullImagePath = null;

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

        string fullPath = buffer.ToString(0, (int)capacity);
        if (string.IsNullOrWhiteSpace(fullPath))
            return false;

        // PATH_IS_NEVER_IDENTITY: the caller never returns fullPath as part of any public fact --
        // it is used ONLY, internally, as (1) the basename source below and (2) the WinVerifyTrust
        // input path for a definitively unpackaged process (see SIGNATURE_INSPECTION_GATE above).
        string baseName = Path.GetFileNameWithoutExtension(fullPath);
        if (string.IsNullOrWhiteSpace(baseName))
            return false;

        processName = baseName;
        fullImagePath = fullPath;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint dwFileAttributes;
        public FILETIME ftCreationTime;
        public FILETIME ftLastAccessTime;
        public FILETIME ftLastWriteTime;
        public uint dwVolumeSerialNumber;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint nNumberOfLinks;
        public uint nFileIndexHigh;
        public uint nFileIndexLow;
    }

    // PRIVON 0.3.0 Gate 1B -- process-bound signer-evidence reuse primitives: kernel-object
    // identity comparison (CompareObjectHandles, never PID), liveness/termination detection,
    // creation-time corroboration, and handle duplication for retention. Kept in its own nested
    // class, matching this file's established per-concern discipline -- never merged into
    // ProcessIdentityNativeMethods (a different concern: single-call identity resolution, not
    // cross-call retained-evidence reuse).
    private static class ReuseNativeMethods
    {
        // CompareObjectHandles requires Windows 10 version 1607 (Anniversary Update) or later.
        // Despite MSDN listing "Kernel32.dll", it is actually exported from KernelBase.dll on real
        // Windows systems (empirically verified: GetProcAddress against kernel32.dll returns NULL,
        // against KernelBase.dll it resolves) -- kernel32.dll carries no forwarder for it that
        // GetProcAddress/DllImport binding can follow, even though normal API-set redirection
        // otherwise makes "kernel32.dll" work transparently for most other kernel32 APIs.
        [DllImport("KernelBase.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CompareObjectHandles(nint hFirstObjectHandle, nint hSecondObjectHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DuplicateHandle(
            nint hSourceProcessHandle, nint hSourceHandle, nint hTargetProcessHandle,
            out nint lpTargetHandle, uint dwDesiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwOptions);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetExitCodeProcess(nint hProcess, out uint lpExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetProcessTimes(
            nint hProcess, out FILETIME lpCreationTime, out FILETIME lpExitTime,
            out FILETIME lpKernelTime, out FILETIME lpUserTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileInformationByHandle(nint hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(nint hObject);
    }

    // PRIVON 0.3.0 Gate 1B -- the executable-file-handle surface used ONLY for retained
    // signer-evidence's own file-identity binding (opening the file for identity comparison, never
    // for verification itself -- that is Win32ExecutableSignatureVerifier's own, separate, file
    // handle). Deliberately its own nested class rather than reusing ReuseNativeMethods.CloseHandle
    // -- CreateFileW is a distinct concern (file objects, not process/handle-comparison primitives).
    private static class FileIdentityNativeMethods
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
