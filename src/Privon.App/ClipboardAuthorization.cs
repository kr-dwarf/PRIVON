using Privon.Windows;

namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate 031F5A (frozen contract, section 5) / Gate 031F5C (this type) -- the mutually
/// exclusive routing result <see cref="ClipboardAuthorizationRouter"/> produces for one fresh
/// <see cref="ForegroundTargetSnapshot"/>, consumed by <see cref="ClipboardPrivacyCoordinator"/>
/// and <see cref="ClipboardDecisionActionResolver"/> alike. A tagged union over the two already-
/// frozen target enums -- <see cref="SupportedTarget"/> (unwidened) and
/// <see cref="SupportedWebTarget"/> -- never a third, new enum.
///
/// STRUCTURAL_MUTUAL_EXCLUSION: <see cref="Kind"/> is settable only by <see cref="ForWindows"/>/
/// <see cref="ForWeb"/>, each of which populates exactly one payload; there is no public
/// constructor and no public setter, so no caller can ever construct an instance carrying both a
/// Windows and a Web payload. <see cref="TryGetWindows"/>/<see cref="TryGetWeb"/> are the only
/// readers, and each refuses outside its own <see cref="Kind"/>.
///
/// <see cref="Outside"/> is <see langword="default"/> -- <see cref="Kind"/>'s own zero value
/// (<see cref="ClipboardAuthorizationKind.Outside"/>) already means "not authorized", so a
/// default-initialized instance is safe without any extra guard.
///
/// A Web authorization is unrepresentable without a bound <see cref="IClipboardAuthorizationFreshness"/>
/// -- <see cref="ForWeb"/> null-checks it -- while a Windows authorization carries none at all (the
/// coordinator/resolver pass <see langword="null"/> straight through to Privon.Windows, never a
/// dummy always-true verifier).
///
/// FACTS_ONLY: this type knows no product policy, no origin, no browser/publisher constant, and no
/// Native Messaging concept -- it carries only the two existing enums and an opaque verifier
/// reference.
/// </summary>
internal readonly record struct ClipboardAuthorization
{
    public ClipboardAuthorizationKind Kind { get; private init; }

    private SupportedTarget WindowsTarget { get; init; }
    private SupportedWebTarget WebTarget { get; init; }
    private IClipboardAuthorizationFreshness? WebFreshness { get; init; }

    /// <summary>Not authorized -- the default value, and the only value <see cref="TryGetWindows"/>/
    /// <see cref="TryGetWeb"/> both refuse.</summary>
    public static ClipboardAuthorization Outside => default;

    public static ClipboardAuthorization ForWindows(SupportedTarget target)
    {
        if (!Enum.IsDefined(target))
            throw new ArgumentOutOfRangeException(nameof(target), target, "SupportedTarget must be a defined, nonzero value.");

        return new ClipboardAuthorization
        {
            Kind = ClipboardAuthorizationKind.WindowsTarget,
            WindowsTarget = target,
        };
    }

    public static ClipboardAuthorization ForWeb(SupportedWebTarget target, IClipboardAuthorizationFreshness freshness)
    {
        if (!Enum.IsDefined(target))
            throw new ArgumentOutOfRangeException(nameof(target), target, "SupportedWebTarget must be a defined, nonzero value.");
        ArgumentNullException.ThrowIfNull(freshness);

        return new ClipboardAuthorization
        {
            Kind = ClipboardAuthorizationKind.WebTarget,
            WebTarget = target,
            WebFreshness = freshness,
        };
    }

    public bool TryGetWindows(out SupportedTarget target)
    {
        if (Kind == ClipboardAuthorizationKind.WindowsTarget)
        {
            target = WindowsTarget;
            return true;
        }

        target = default;
        return false;
    }

    public bool TryGetWeb(out SupportedWebTarget target, out IClipboardAuthorizationFreshness freshness)
    {
        if (Kind == ClipboardAuthorizationKind.WebTarget)
        {
            target = WebTarget;
            freshness = WebFreshness!;
            return true;
        }

        target = default;
        freshness = null!;
        return false;
    }
}
