namespace Privon.UsOptInHost;

// [PRIVON-AI-HANDOFF]
// ROLE: The host command line. The experiment runs ONLY when asked for by name; anything else starts nothing.
// TRUTH: --experimental-us-opt-in is the explicit opt-in. --storage-root <path> optionally points the whole app (settings, country choice, keys) at a
//        directory other than the real %LocalAppData%\PRIVON, so an experiment need not touch real settings.
// FROZEN: An unknown argument, a missing value or a missing opt-in is "not opted in" -- never a guess.
// DO_NOT: Read the opt-in from the environment or a file, or default it on.

public sealed record HostOptions(bool ExplicitOptIn, string? StorageRoot)
{
    public const string OptInFlag = "--experimental-us-opt-in";
    public const string StorageRootFlag = "--storage-root";

    public static HostOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        bool optIn = false;
        string? root = null;

        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case OptInFlag:
                    optIn = true;
                    break;
                case StorageRootFlag:
                    if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                        return new HostOptions(false, null);
                    root = args[++i];
                    break;
                default:
                    return new HostOptions(false, null);   // an argument this host does not know: do not start on a guess
            }
        }

        return new HostOptions(optIn, optIn ? root : null);
    }
}
