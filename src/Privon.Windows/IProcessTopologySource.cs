using Microsoft.Win32.SafeHandles;

namespace Privon.Windows;

/// <summary>
/// PRIVON 0.3.1 Gate 031F6F (E2) -- test seam over the Win32 browser-host process-topology
/// primitives (open/inspect/retain, image path, creation time, parent PID, package identity,
/// executable-signature resolution, liveness), matching this codebase's existing native-seam
/// pattern (<see cref="IForegroundTargetSource"/>, <see cref="IExecutableSignatureVerifier"/>): an
/// interface with no policy of its own, so <see cref="BrowserHostBindingResolver"/>'s own
/// ordering/failure/topology logic can be exercised deterministically against a test double instead
/// of real OS processes. The only production implementation is <see cref="Win32ProcessTopologySource"/>.
///
/// HANDLE_BOUND (frozen): every fact-returning member below takes an already-open
/// <see cref="SafeProcessHandle"/> the caller obtained from <see cref="TryOpenForInspection"/> or
/// <see cref="TryOpenForRetention"/> -- never a raw PID re-lookup, mirroring
/// <see cref="Win32ForegroundTargetSource"/>'s own SINGLE_HANDLE_IDENTITY discipline.
///
/// FACTS_ONLY: this interface knows no browser/publisher product policy, no Web origin/channel/
/// revision/epoch, and no Native Messaging concept of any kind -- that judgment belongs exclusively
/// to a future <c>Privon.App</c>-owned gate.
/// </summary>
internal interface IProcessTopologySource
{
    /// <summary>Opens <paramref name="processId"/> with the minimum rights needed to inspect its
    /// mechanical facts (PROCESS_QUERY_LIMITED_INFORMATION) -- never SYNCHRONIZE. Used for the host
    /// (which is never retained).</summary>
    bool TryOpenForInspection(uint processId, out SafeProcessHandle? handle);

    /// <summary>Opens <paramref name="processId"/> with rights sufficient for both inspection AND
    /// future liveness waits (PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE). Used for every
    /// browser-candidate position (the immediate parent and, in a CommandIntermediary topology, the
    /// grandparent) -- either of which might become the retained browser.</summary>
    bool TryOpenForRetention(uint processId, out SafeProcessHandle? handle);

    /// <summary>The process's full executable image path, from the supplied handle alone.</summary>
    bool TryGetImagePath(SafeProcessHandle handle, out string? imagePath);

    /// <summary>The process's creation time, as a monotonic tick count comparable only against other
    /// values from this same method, from the supplied handle alone.</summary>
    bool TryGetCreationTimeTicks(SafeProcessHandle handle, out long creationTimeTicks);

    /// <summary>The process's PARENT process ID (NtQueryInformationProcess(ProcessBasicInformation).
    /// InheritedFromUniqueProcessId-equivalent), from the supplied handle alone -- never an ancestor
    /// enumeration.</summary>
    bool TryGetParentProcessId(SafeProcessHandle handle, out uint parentProcessId);

    /// <summary>The mechanical Windows package-identity fact for the supplied handle -- see
    /// <see cref="PackageIdentityResolution"/>'s own tri-state doc.</summary>
    PackageIdentityResolution ResolvePackageIdentity(SafeProcessHandle handle, out string? packageFamilyName);

    /// <summary>Resolves the executable-signature mechanical fact for the supplied (retained) handle
    /// and its already-known image path -- the caller is solely responsible for only invoking this
    /// once, for the SELECTED browser candidate, and only when its package identity is
    /// <see cref="PackageIdentityResolution.NoPackage"/> (SIGNATURE_INSPECTION_GATE).</summary>
    ExecutableSignatureResolution ResolveSignature(SafeProcessHandle handle, string imagePath, out string? signerOrganization);

    /// <summary>The tri-state liveness fact for the supplied (retained) handle -- see
    /// <see cref="RetainedProcessLiveness"/>'s own doc.</summary>
    RetainedProcessLiveness CheckLiveness(SafeProcessHandle handle);
}
