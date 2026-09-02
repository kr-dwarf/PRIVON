namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate 031F5A (frozen contract) / Gate 031F5C (this type) -- the discriminator for
/// <see cref="ClipboardAuthorization"/>. <see cref="Outside"/> is deliberately the zero/default
/// value, so a default-initialized <see cref="ClipboardAuthorization"/> is never mistaken for an
/// authorized target of any kind.
/// </summary>
internal enum ClipboardAuthorizationKind
{
    Outside = 0,
    WindowsTarget = 1,
    WebTarget = 2,
}
