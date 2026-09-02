namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate 031F6I (E3) -- the single process-launch dispatch point
/// <see cref="Program.Main"/> delegates to. Classifies <paramref name="args"/> via
/// <see cref="NativeMessagingHostInvocation.Classify"/> and invokes EXACTLY ONE of
/// <paramref name="normalRunner"/>/<paramref name="hostRunner"/> for
/// <see cref="NativeMessagingInvocationKind.Normal"/>/<see cref="NativeMessagingInvocationKind.Host"/>,
/// and NEITHER for <see cref="NativeMessagingInvocationKind.RejectedHostShaped"/> (Gate 031F6H R1/R2/
/// R3) -- a rejected host-shaped invocation never silently falls through to the tray/WPF path. Both
/// runners are plain synchronous delegates so this type, and the classification it drives, can be
/// exercised deterministically without ever constructing a real WPF <c>Application</c> or a real host
/// relay loop.
/// </summary>
public static class PrivonEntryPoint
{
    public static void Run(
        string[] args,
        IReadOnlySet<string> allowedExtensionOrigins,
        Action normalRunner,
        Action hostRunner)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(allowedExtensionOrigins);
        ArgumentNullException.ThrowIfNull(normalRunner);
        ArgumentNullException.ThrowIfNull(hostRunner);

        var invocation = NativeMessagingHostInvocation.Classify(args, allowedExtensionOrigins);

        switch (invocation.Kind)
        {
            case NativeMessagingInvocationKind.Normal:
                normalRunner();
                break;

            case NativeMessagingInvocationKind.Host:
                hostRunner();
                break;

            case NativeMessagingInvocationKind.RejectedHostShaped:
                // Neither runner invoked -- never falls through to the tray runner (Gate 031F6H R3).
                break;
        }
    }
}
