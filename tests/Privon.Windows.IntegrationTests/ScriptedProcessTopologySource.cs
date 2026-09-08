using Microsoft.Win32.SafeHandles;
using Privon.Windows;

namespace Privon.Windows.IntegrationTests;

// PRIVON 0.3.1 Gate 031F6F (E2 GREEN) -- deterministic, OS-free double for the real, now-existing
// Privon.Windows.IProcessTopologySource seam (browser-host process binding mechanics: open-for-
// inspection, open-for-retention, image paths, creation times, NtQueryInformationProcess-shaped
// parent lookup, package identity, executable-signature facts, liveness, call history, handle
// disposal).
//
// HANDLE-ENCODED PID: every synthetic SafeProcessHandle this double returns encodes its owning PID
// in the raw handle value (inspection handles: 2_000_000 + pid; retention handles: 3_000_000 + pid)
// so every subsequent fact-returning call -- which the real interface deliberately takes a
// SafeProcessHandle for, never a bare PID (HANDLE_BOUND, see IProcessTopologySource's own doc) --
// can still be answered from this fake's PID-keyed script data without violating that contract.
//
// SAFE_HANDLE_PATTERN (frozen): every returned handle is a synthetic, non-owning SafeProcessHandle
// -- new SafeProcessHandle(syntheticNonZeroIntPtr, ownsHandle: false) -- never a bogus native
// CloseHandle. Disposal is observable via the handle's own IsClosed after Dispose(), matching this
// gate's own SAFEHANDLE_FAKE contract.
internal sealed class ScriptedProcessTopologySource : IProcessTopologySource
{
    public sealed class ProcessNodeScript
    {
        public bool OpenForInspectionSucceeds { get; set; } = true;
        public bool OpenForRetentionSucceeds { get; set; } = true;

        public string? ImagePath { get; set; }

        /// <summary>Null models "process creation time unavailable" (R7/R8).</summary>
        public long? CreationTimeTicks { get; set; }

        /// <summary>Models NtQueryInformationProcess(ProcessBasicInformation).InheritedFromUniqueProcessId.
        /// False models "parent query unavailable" (R8), independent of the value itself.</summary>
        public bool ParentQuerySucceeds { get; set; } = true;
        public uint ParentProcessId { get; set; }

        public string? ProcessName { get; set; }

        public PackageIdentityResolution PackageIdentity { get; set; } = PackageIdentityResolution.NoPackage;
        public string? PackageFamilyName { get; set; }

        public ExecutableSignatureResolution SignatureResult { get; set; } = ExecutableSignatureResolution.Trusted;
        public string? SignerOrganization { get; set; }

        /// <summary>"Alive" / "Exited" / "Unavailable" -- mapped to RetainedProcessLiveness.</summary>
        public string Liveness { get; set; } = "Alive";
    }

    private const int InspectionOffset = 2_000_000;
    private const int RetentionOffset = 3_000_000;

    public Dictionary<uint, ProcessNodeScript> Nodes { get; } = [];

    public List<string> CallLog { get; } = [];

    public int SignatureResolutionCallCount { get; private set; }
    public List<uint> SignatureResolvedPids { get; } = [];

    /// <summary>Every SafeProcessHandle this double has ever issued via TryOpenForInspection/
    /// TryOpenForRetention, keyed by PID -- lets a test observe .IsClosed after resolution completes,
    /// since the fake itself never calls a native CloseHandle (SAFEHANDLE_FAKE, section 31).</summary>
    public Dictionary<uint, SafeProcessHandle> IssuedInspectionHandles { get; } = [];
    public Dictionary<uint, SafeProcessHandle> IssuedRetentionHandles { get; } = [];

    public ProcessNodeScript AddNode(uint pid, ProcessNodeScript? script = null)
    {
        script ??= new ProcessNodeScript();
        Nodes[pid] = script;
        return script;
    }

    private ProcessNodeScript RequireNode(uint pid)
    {
        if (!Nodes.TryGetValue(pid, out var node))
            throw new InvalidOperationException($"ScriptedProcessTopologySource: no node scripted for PID {pid}.");
        return node;
    }

    private static uint DecodePid(SafeProcessHandle handle)
    {
        long raw = handle.DangerousGetHandle().ToInt64();
        if (raw >= RetentionOffset)
            return (uint)(raw - RetentionOffset);
        return (uint)(raw - InspectionOffset);
    }

    public bool TryOpenForInspection(uint processId, out SafeProcessHandle? handle)
    {
        CallLog.Add($"OpenForInspection({processId})");
        handle = null;
        if (!Nodes.TryGetValue(processId, out var node) || !node.OpenForInspectionSucceeds)
            return false;

        handle = new SafeProcessHandle(new IntPtr(checked(InspectionOffset + (int)processId)), ownsHandle: false);
        IssuedInspectionHandles[processId] = handle;
        return true;
    }

    public bool TryOpenForRetention(uint processId, out SafeProcessHandle? handle)
    {
        CallLog.Add($"OpenForRetention({processId})");
        handle = null;
        if (!Nodes.TryGetValue(processId, out var node) || !node.OpenForRetentionSucceeds)
            return false;

        handle = new SafeProcessHandle(new IntPtr(checked(RetentionOffset + (int)processId)), ownsHandle: false);
        IssuedRetentionHandles[processId] = handle;
        return true;
    }

    public bool TryGetImagePath(SafeProcessHandle handle, out string? imagePath)
    {
        uint pid = DecodePid(handle);
        CallLog.Add($"GetImagePath({pid})");
        imagePath = RequireNode(pid).ImagePath;
        return !string.IsNullOrEmpty(imagePath);
    }

    public bool TryGetCreationTimeTicks(SafeProcessHandle handle, out long creationTimeTicks)
    {
        uint pid = DecodePid(handle);
        CallLog.Add($"GetCreationTime({pid})");
        var node = RequireNode(pid);
        creationTimeTicks = node.CreationTimeTicks ?? 0;
        return node.CreationTimeTicks.HasValue;
    }

    /// <summary>Models NtQueryInformationProcess(ProcessBasicInformation).InheritedFromUniqueProcessId.</summary>
    public bool TryGetParentProcessId(SafeProcessHandle handle, out uint parentProcessId)
    {
        uint pid = DecodePid(handle);
        CallLog.Add($"GetParentProcessId({pid})");
        var node = RequireNode(pid);
        parentProcessId = node.ParentQuerySucceeds ? node.ParentProcessId : 0;
        return node.ParentQuerySucceeds;
    }

    public PackageIdentityResolution ResolvePackageIdentity(SafeProcessHandle handle, out string? packageFamilyName)
    {
        uint pid = DecodePid(handle);
        CallLog.Add($"ResolvePackageIdentity({pid})");
        var node = RequireNode(pid);
        packageFamilyName = node.PackageFamilyName;
        return node.PackageIdentity;
    }

    /// <summary>SIGNATURE_INSPECTION_GATE (E2-R16/R21): the caller (BrowserHostBindingResolver) is
    /// responsible for only calling this when PackageIdentity == NoPackage, and only for the
    /// SELECTED browser process, never for the host or an intermediary cmd -- this double just
    /// faithfully counts every call and which PID it was for, so a test can prove the caller honored
    /// that gate.</summary>
    public ExecutableSignatureResolution ResolveSignature(SafeProcessHandle handle, string imagePath, out string? signerOrganization)
    {
        uint pid = DecodePid(handle);
        CallLog.Add($"ResolveSignature({pid})");
        SignatureResolutionCallCount++;
        SignatureResolvedPids.Add(pid);
        var node = RequireNode(pid);
        signerOrganization = node.SignerOrganization;
        return node.SignatureResult;
    }

    public RetainedProcessLiveness CheckLiveness(SafeProcessHandle handle)
    {
        uint pid = DecodePid(handle);
        CallLog.Add($"CheckLiveness({pid})");
        return RequireNode(pid).Liveness switch
        {
            "Alive" => RetainedProcessLiveness.Alive,
            "Exited" => RetainedProcessLiveness.Exited,
            _ => RetainedProcessLiveness.Unavailable,
        };
    }
}
