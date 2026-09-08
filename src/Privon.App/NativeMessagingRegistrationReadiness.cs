namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate E5G.P3 -- the six mutually exclusive states
/// <see cref="NativeMessagingHostRegistrationCoordinator"/> can ever report. Ownership is determined
/// EXCLUSIVELY by the exact registry witness <see cref="NativeMessagingHostRegistrar"/> already froze
/// (Gate E5D) -- manifest content is consulted only once that witness already proves ownership, to
/// distinguish <see cref="Ready"/> from <see cref="OwnedNeedsRepair"/>. Manifest content can never
/// promote a <see cref="ForeignBlocked"/> or <see cref="OrphanBlocked"/> state into <see cref="Ready"/>,
/// no matter how exactly it happens to match.
/// </summary>
internal enum NativeMessagingRegistrationReadiness
{
    /// <summary>Leaf absent AND the expected manifest is absent -- the only state Provision may
    /// mutate from.</summary>
    Fresh,

    /// <summary>Registry witness proven AND the manifest exists AND its content matches the supplied
    /// expected spec (host path + extension origin) exactly.</summary>
    Ready,

    /// <summary>Registry witness proven, but the manifest is absent OR its content does not match the
    /// supplied expected spec -- the only state Repair may mutate from.</summary>
    OwnedNeedsRepair,

    /// <summary>The leaf exists but its default value does not exactly (Ordinal) match the expected
    /// manifest path -- a third party's registration. Never mutated.</summary>
    ForeignBlocked,

    /// <summary>The leaf is absent, but a file already exists at the expected manifest path with no
    /// registry witness proving PRIVON put it there. Never mutated.</summary>
    OrphanBlocked,

    /// <summary>Unusable explicit input (host path or extension origin), or a mechanical
    /// exception while inspecting or mutating. Never a success.</summary>
    Failed,
}
