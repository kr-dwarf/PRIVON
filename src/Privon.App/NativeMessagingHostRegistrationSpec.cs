namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate E5D.C -- the composition input a future caller (E5F/E5G) supplies to
/// <see cref="NativeMessagingHostRegistrar.Install"/>: exactly the two semantic values the manifest
/// needs that the registrar itself cannot know -- the real host executable's path, and the ONE
/// already-verified browser extension origin to authorize. Deliberately carries NEITHER a manifest
/// path (that remains registrar-owned and deterministic -- see
/// <see cref="NativeMessagingHostRegistrar"/>'s own expected-path formula) NOR a browser identity
/// (that is <see cref="NativeMessagingHostRegistrar.Install"/>'s own separate parameter) NOR a
/// collection of origins -- one Install call always corresponds to exactly one browser and exactly
/// one verified origin, never a production-wide allowlist.
/// </summary>
internal sealed record NativeMessagingHostRegistrationSpec(string HostExecutablePath, string ExtensionOrigin);
