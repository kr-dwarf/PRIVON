namespace Privon.Windows;

/// <summary>
/// PRIVON 0.3.1 Gate 031F6F (E2) -- the mechanical, product-policy-free outcome of
/// <see cref="BrowserHostBindingResolver.Resolve"/>. Matches this codebase's established
/// "safe value first" discipline (<see cref="PackageIdentityResolution.Unresolved"/>,
/// <see cref="ExecutableSignatureResolution.NotInspected"/>): a caller that forgets to check this
/// value, or receives a default-initialized result, never mistakes silence for a successful binding.
/// Every non-<see cref="Resolved"/> member is an ordinary, expected, non-crashing condition -- never
/// an exception -- and always leaves the resolver's <c>binding</c> out-parameter <see langword="null"/>
/// with every handle acquired along that path already disposed.
/// </summary>
public enum BrowserHostBindingStatus
{
    /// <summary>Default/never actually returned by a completed <see cref="BrowserHostBindingResolver.Resolve"/>
    /// call -- the safe zero value, matching this codebase's own "safe value first" convention.</summary>
    Unresolved = 0,

    /// <summary>The supplied host process ID was zero.</summary>
    HostProcessIdInvalid,

    /// <summary>The host process could not be opened for inspection.</summary>
    HostOpenFailed,

    /// <summary>The host's executable image path could not be determined.</summary>
    HostImagePathUnavailable,

    /// <summary>The host's executable image path did not match the injected expected host path.</summary>
    HostImagePathMismatch,

    /// <summary>The host's process creation time could not be determined.</summary>
    HostCreationTimeUnavailable,

    /// <summary>The host's parent process ID could not be determined (NtQueryInformationProcess failed).</summary>
    ParentProcessIdUnavailable,

    /// <summary>The host's parent process ID was zero.</summary>
    ParentProcessIdInvalid,

    /// <summary>The immediate parent (browser-or-cmd candidate) could not be opened.</summary>
    ParentOpenFailed,

    /// <summary>Strict creation-time ordering (browser &lt; cmd &lt; host, or browser &lt; host) was
    /// violated at some hop, or a required creation time at a non-host hop was unavailable -- no
    /// tolerance, no epsilon.</summary>
    CreationOrderingViolation,

    /// <summary>The immediate parent is neither a plausible browser candidate nor the exact System32
    /// cmd.exe intermediary, OR the CommandIntermediary grandparent is itself the exact System32
    /// cmd.exe (a second cmd hop) -- fail closed rather than guessing either interpretation.</summary>
    UnknownIntermediary,

    /// <summary>The cmd intermediary's parent process ID (the browser-candidate grandparent) could not
    /// be determined.</summary>
    GrandparentProcessIdUnavailable,

    /// <summary>The cmd intermediary's parent process ID was zero.</summary>
    GrandparentProcessIdInvalid,

    /// <summary>The grandparent (browser candidate in a CommandIntermediary topology) could not be
    /// opened.</summary>
    GrandparentOpenFailed,

    /// <summary>The selected browser candidate's own identity (process name) could not be established.</summary>
    BrowserIdentityUnresolved,

    /// <summary>The selected browser candidate's retained handle reported NOT alive immediately at
    /// resolution time.</summary>
    BrowserAlreadyExited,

    /// <summary>A <see cref="BrowserHostBinding"/> was successfully resolved and is owned by the
    /// resolver's out-parameter.</summary>
    Resolved,
}
