using System.IO;
using Microsoft.Win32.SafeHandles;

namespace Privon.Windows;

/// <summary>
/// PRIVON 0.3.1 Gate 031F6F (E2) -- resolves the mechanical browser-host process binding for a
/// given host process ID: DIRECT (browser -&gt; host) or CommandIntermediary (browser -&gt; exact
/// System32 cmd.exe -&gt; host), structurally bounded to exactly those two shapes (no recursion, no
/// depth configuration, no ancestor loop). See <see cref="BrowserHostBindingStatus"/> for the full
/// fail-closed vocabulary and <see cref="IProcessTopologySource"/> for the mechanical seam this type
/// orchestrates but never implements itself.
///
/// PARENT_API (frozen): parent PID comes exclusively from <see cref="IProcessTopologySource.TryGetParentProcessId"/>
/// (NtQueryInformationProcess(ProcessBasicInformation).InheritedFromUniqueProcessId in the real Win32
/// implementation) -- never a process-table enumeration.
///
/// HANDLE_DISCIPLINE (frozen): the host is opened for inspection only (it is never retained). Each
/// browser-CANDIDATE position (the immediate parent, and -- only in a CommandIntermediary topology --
/// the grandparent) is opened once, for RETENTION rights, because either might become the actual
/// retained browser; a candidate that turns out to be the cmd intermediary has its retention handle
/// disposed as a temporary, never exposed. Every failure path disposes every handle it acquired and
/// leaves the <c>binding</c> out-parameter <see langword="null"/>; only <see cref="BrowserHostBindingStatus.Resolved"/>
/// ever transfers browser handle ownership out (into the returned <see cref="BrowserHostBinding"/>'s
/// own <see cref="RetainedProcess"/>).
///
/// SINGLE_PID_SOURCE (frozen): the selected browser's PID is read from the SafeProcessHandle this
/// resolver itself already opened for that exact candidate -- there is never a second, independently
/// supplied PID value; see <see cref="BrowserHostBinding"/>'s own SINGLE_PID_SOURCE doc.
/// </summary>
public sealed class BrowserHostBindingResolver
{
    private readonly string _expectedHostExecutablePath;
    private readonly string _systemDirectory;
    private readonly string _expectedCmdPath;
    private readonly IProcessTopologySource _source;

    public BrowserHostBindingResolver(string expectedHostExecutablePath, string systemDirectory)
        : this(expectedHostExecutablePath, systemDirectory, new Win32ProcessTopologySource())
    {
    }

    internal BrowserHostBindingResolver(string expectedHostExecutablePath, string systemDirectory, IProcessTopologySource source)
    {
        ArgumentNullException.ThrowIfNull(expectedHostExecutablePath);
        ArgumentNullException.ThrowIfNull(systemDirectory);
        ArgumentNullException.ThrowIfNull(source);

        if (expectedHostExecutablePath.Length == 0)
            throw new ArgumentException("expectedHostExecutablePath must not be empty.", nameof(expectedHostExecutablePath));
        if (systemDirectory.Length == 0)
            throw new ArgumentException("systemDirectory must not be empty.", nameof(systemDirectory));
        if (!Path.IsPathRooted(expectedHostExecutablePath))
            throw new ArgumentException("expectedHostExecutablePath must be a rooted (absolute) path.", nameof(expectedHostExecutablePath));
        if (!Path.IsPathRooted(systemDirectory))
            throw new ArgumentException("systemDirectory must be a rooted (absolute) path.", nameof(systemDirectory));

        _expectedHostExecutablePath = Path.GetFullPath(expectedHostExecutablePath);
        _systemDirectory = Path.GetFullPath(systemDirectory);
        _expectedCmdPath = Path.GetFullPath(Path.Combine(_systemDirectory, "cmd.exe"));
        _source = source;
    }

    public BrowserHostBindingStatus Resolve(uint hostPid, out BrowserHostBinding? binding)
    {
        binding = null;

        if (hostPid == 0)
            return BrowserHostBindingStatus.HostProcessIdInvalid;

        if (!_source.TryOpenForInspection(hostPid, out var hostHandle) || hostHandle is null)
            return BrowserHostBindingStatus.HostOpenFailed;

        using (hostHandle)
        {
            if (!_source.TryGetImagePath(hostHandle, out string? hostImagePath) || string.IsNullOrEmpty(hostImagePath))
                return BrowserHostBindingStatus.HostImagePathUnavailable;

            if (!string.Equals(Path.GetFullPath(hostImagePath), _expectedHostExecutablePath, StringComparison.OrdinalIgnoreCase))
                return BrowserHostBindingStatus.HostImagePathMismatch;

            if (!_source.TryGetCreationTimeTicks(hostHandle, out long hostCreationTicks))
                return BrowserHostBindingStatus.HostCreationTimeUnavailable;

            if (!_source.TryGetParentProcessId(hostHandle, out uint parentPid))
                return BrowserHostBindingStatus.ParentProcessIdUnavailable;
            if (parentPid == 0)
                return BrowserHostBindingStatus.ParentProcessIdInvalid;

            if (!_source.TryOpenForRetention(parentPid, out var parentHandle) || parentHandle is null)
                return BrowserHostBindingStatus.ParentOpenFailed;

            bool parentTransferred = false;
            try
            {
                if (!_source.TryGetImagePath(parentHandle, out string? parentImagePath) || string.IsNullOrEmpty(parentImagePath))
                    return BrowserHostBindingStatus.UnknownIntermediary;

                if (!_source.TryGetCreationTimeTicks(parentHandle, out long parentCreationTicks))
                    return BrowserHostBindingStatus.CreationOrderingViolation;
                if (parentCreationTicks >= hostCreationTicks)
                    return BrowserHostBindingStatus.CreationOrderingViolation;

                bool parentIsExactCmd = IsExactSystem32Cmd(parentImagePath);

                if (!parentIsExactCmd)
                {
                    var status = ResolveBrowserCandidate(
                        parentHandle, parentPid, BrowserHostTopology.Direct, out binding);
                    if (status == BrowserHostBindingStatus.Resolved)
                        parentTransferred = true;
                    return status;
                }

                // CMD FLOW -- parentHandle is the cmd intermediary's own (retention-rights) handle;
                // it is NEVER the retained browser and is always disposed as a temporary below.
                if (!_source.TryGetParentProcessId(parentHandle, out uint grandparentPid))
                    return BrowserHostBindingStatus.GrandparentProcessIdUnavailable;
                if (grandparentPid == 0)
                    return BrowserHostBindingStatus.GrandparentProcessIdInvalid;

                if (!_source.TryOpenForRetention(grandparentPid, out var grandparentHandle) || grandparentHandle is null)
                    return BrowserHostBindingStatus.GrandparentOpenFailed;

                bool grandparentTransferred = false;
                try
                {
                    if (!_source.TryGetImagePath(grandparentHandle, out string? grandparentImagePath) || string.IsNullOrEmpty(grandparentImagePath))
                        return BrowserHostBindingStatus.UnknownIntermediary;

                    // R3: a second cmd hop at the browser-candidate position is never a valid browser.
                    if (IsExactSystem32Cmd(grandparentImagePath))
                        return BrowserHostBindingStatus.UnknownIntermediary;

                    if (!_source.TryGetCreationTimeTicks(grandparentHandle, out long grandparentCreationTicks))
                        return BrowserHostBindingStatus.CreationOrderingViolation;
                    if (grandparentCreationTicks >= parentCreationTicks)
                        return BrowserHostBindingStatus.CreationOrderingViolation;

                    var status = ResolveBrowserCandidate(
                        grandparentHandle, grandparentPid, BrowserHostTopology.CommandIntermediary, out binding);
                    if (status == BrowserHostBindingStatus.Resolved)
                        grandparentTransferred = true;
                    return status;
                }
                finally
                {
                    if (!grandparentTransferred)
                        grandparentHandle.Dispose();
                }
            }
            finally
            {
                if (!parentTransferred)
                    parentHandle.Dispose();
            }
        }
    }

    /// <summary>Resolves the mechanical facts for a candidate that has already passed topology/
    /// ordering classification, and builds the final <see cref="BrowserHostBinding"/> on success.
    /// <paramref name="candidateHandle"/> ownership transfers into the returned binding's
    /// <see cref="RetainedProcess"/> ONLY when this method returns <see cref="BrowserHostBindingStatus.Resolved"/>
    /// -- the caller remains responsible for disposing it on every other outcome.</summary>
    private BrowserHostBindingStatus ResolveBrowserCandidate(
        SafeProcessHandle candidateHandle, uint candidatePid, BrowserHostTopology topology, out BrowserHostBinding? binding)
    {
        binding = null;

        if (!_source.TryGetImagePath(candidateHandle, out string? imagePath) || string.IsNullOrEmpty(imagePath))
            return BrowserHostBindingStatus.BrowserIdentityUnresolved;

        string processName = Path.GetFileNameWithoutExtension(imagePath);
        if (string.IsNullOrWhiteSpace(processName))
            return BrowserHostBindingStatus.BrowserIdentityUnresolved;

        var packageIdentity = _source.ResolvePackageIdentity(candidateHandle, out string? packageFamilyName);

        // SIGNATURE_INSPECTION_GATE (frozen): only ever attempted for a definitively unpackaged
        // candidate -- see ExecutableSignatureResolution's own NotInspected doc.
        var signature = ExecutableSignatureResolution.NotInspected;
        string? signerOrganization = null;
        if (packageIdentity == PackageIdentityResolution.NoPackage)
            signature = _source.ResolveSignature(candidateHandle, imagePath, out signerOrganization);

        if (_source.CheckLiveness(candidateHandle) != RetainedProcessLiveness.Alive)
            return BrowserHostBindingStatus.BrowserAlreadyExited;

        var retained = new RetainedProcess(candidateHandle, candidatePid, _source);
        binding = new BrowserHostBinding(
            retained, processName, packageIdentity, packageFamilyName, signature, signerOrganization, topology);
        return BrowserHostBindingStatus.Resolved;
    }

    private bool IsExactSystem32Cmd(string candidateImagePath) =>
        string.Equals(Path.GetFullPath(candidateImagePath), _expectedCmdPath, StringComparison.OrdinalIgnoreCase);
}
