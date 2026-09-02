using System.Text.RegularExpressions;

namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate 031F6I (E3) -- classifies a process launch's command-line arguments against the
/// real Chrome/Edge Native Messaging host invocation shape (Gate 031F6H R1/R2/R3): a
/// <c>chrome-extension://&lt;32 chars [a-p]&gt;/</c> origin argument, optionally followed by a
/// <c>--parent-window=...</c> flag -- the exact facts Chromium supplies to a native messaging host
/// process. <paramref name="args"/> here is .NET's own <c>Main(string[] args)</c> array (already
/// stripped of argv[0], the executable path) -- so a Chromium invocation's own argv[1] (the extension
/// origin) is <c>args[0]</c>, and argv[2] (<c>--parent-window=...</c>) is <c>args[1]</c>.
///
/// CLASSIFICATION (frozen by this gate's own test harness): zero arguments of any kind is
/// <see cref="NativeMessagingInvocationKind.Normal"/>. Any nonzero argument set is NEVER Normal --
/// it is always either <see cref="NativeMessagingInvocationKind.Host"/> (exactly one non-flag
/// argument, matching the frozen origin pattern, present in <paramref name="allowedExtensionOrigins"/>)
/// or <see cref="NativeMessagingInvocationKind.RejectedHostShaped"/> (anything else: zero or multiple
/// non-flag arguments, a malformed origin, an unallowlisted origin, or a lone
/// <c>--parent-window=...</c> flag with no origin at all) -- so a rejected host-shaped invocation can
/// never silently fall through to the normal tray/WPF path.
/// </summary>
public readonly record struct NativeMessagingHostInvocation(
    NativeMessagingInvocationKind Kind,
    string? ExtensionOrigin)
{
    private const string ParentWindowPrefix = "--parent-window=";

    // Real Chrome extension ID shape: exactly 32 characters drawn from the alphabet a-p.
    private static readonly Regex ExtensionOriginPattern =
        new(@"^chrome-extension://[a-p]{32}/$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static NativeMessagingHostInvocation Classify(string[] args, IReadOnlySet<string> allowedExtensionOrigins)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(allowedExtensionOrigins);

        if (args.Length == 0)
            return new NativeMessagingHostInvocation(NativeMessagingInvocationKind.Normal, null);

        string[] nonFlagArgs = args.Where(a => !a.StartsWith(ParentWindowPrefix, StringComparison.Ordinal)).ToArray();

        if (nonFlagArgs.Length != 1)
            return new NativeMessagingHostInvocation(NativeMessagingInvocationKind.RejectedHostShaped, null);

        string origin = nonFlagArgs[0];
        if (!ExtensionOriginPattern.IsMatch(origin))
            return new NativeMessagingHostInvocation(NativeMessagingInvocationKind.RejectedHostShaped, null);

        if (!allowedExtensionOrigins.Contains(origin))
            return new NativeMessagingHostInvocation(NativeMessagingInvocationKind.RejectedHostShaped, null);

        return new NativeMessagingHostInvocation(NativeMessagingInvocationKind.Host, origin);
    }
}
