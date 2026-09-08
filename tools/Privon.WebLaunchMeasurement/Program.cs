using System.Globalization;
using System.Text.Json;
using Privon.Windows;

namespace Privon.WebLaunchMeasurement;

// PRIVON Gate E5B -- DEV-ONLY, NON-SHIPPING.
//
// This executable is never launched by a user and is never part of the shipping product. It is
// launched BY Chrome/Edge as a TEMPORARY Native Messaging host registered under the host name
// "com.privon.devmeasure" (never "com.privon.host" -- the real production host name is never
// touched by anything in this project).
//
// It records ONLY the mechanical facts Gate E5B needs to measure real browser launch shape:
//   - the raw .NET args array this process was actually launched with (extension origin,
//     --parent-window). This is NOT argv[0] -- .NET's Main(string[] args) is already stripped of
//     the executable path; this process's own image path is recorded completely separately, below,
//     as hostImagePath (an OS self-query, not a re-parse of any argv element).
//   - a raw, independently-observed host/immediate-parent/grandparent ancestry chain (pid, process
//     name, image path, creation time), captured by this tool's own ProcessChainCapture -- never
//     derived from BrowserHostBinding.Topology or any other resolver output (Gate E5B.1).
//   - parent/grandparent process topology, package identity, executable signature and signer
//     organization for the resolved browser -- via Privon.Windows's existing, frozen, PUBLIC
//     BrowserHostBindingResolver surface (Gate 031F6F/E2), kept unchanged and reported separately
//     from the raw ancestry chain above.
//
// It records NO clipboard content, NO page content, NO DOM, NO URL other than the browser-supplied
// extension origin in argv, NO PII, no command line, no environment variable, no window title, and
// performs NO network transmission of any kind. Evidence is written to a single local JSON file only.
internal static class Program
{
    private const string ParentWindowPrefix = "--parent-window=";

    private static void Main(string[] args)
    {
        try
        {
            Run(args);
        }
        catch
        {
            // Best-effort measurement only. This process is launched the same way a real Native
            // Messaging host is (Gate 031F6H R23 precedent): no diagnostic byte of any kind may
            // reach stdout, since Chrome/Edge is watching this process's stdio for native-messaging
            // framing. A measurement failure simply ends the process without writing evidence.
        }
    }

    private static void Run(string[] args)
    {
        string? extensionOriginArg = null;
        bool extensionOriginTrailingSlash = false;
        string? parentWindowRaw = null;
        var unexpectedExtraArgs = new List<string>();

        foreach (string arg in args)
        {
            if (arg.StartsWith(ParentWindowPrefix, StringComparison.Ordinal))
            {
                parentWindowRaw = arg[ParentWindowPrefix.Length..];
            }
            else if (extensionOriginArg is null)
            {
                extensionOriginArg = arg;
                extensionOriginTrailingSlash = arg.EndsWith('/');
            }
            else
            {
                unexpectedExtraArgs.Add(arg);
            }
        }

        int hostProcessId = Environment.ProcessId;
        string? hostImagePath = Environment.ProcessPath;

        // Raw ancestry chain (Gate E5B.1): captured independently of BrowserHostBindingResolver
        // below. Each hop's parent PID comes exclusively from that same hop's own
        // NtQueryInformationProcess call (ProcessChainCapture.CaptureHop), never from a prior or
        // later hop's result and never from the resolver's Topology enum -- see PID_REUSE / SNAPSHOT
        // SAFETY sequencing in ProcessChainCapture's own doc comment.
        var hostHop = ProcessChainCapture.CaptureHop((uint)hostProcessId);

        var parentHop = hostHop.ParentPid is uint parentPid && parentPid != 0
            ? ProcessChainCapture.CaptureHop(parentPid)
            : ProcessHopRecord.NotApplicable();

        var grandparentHop = parentHop.ParentPid is uint grandparentPid && grandparentPid != 0
            ? ProcessChainCapture.CaptureHop(grandparentPid)
            : ProcessHopRecord.NotApplicable();

        // Strict raw-timestamp ordering only -- no threshold, no causal claim beyond the PID chain
        // already established above.
        bool? grandparentBeforeParent =
            grandparentHop.CreationTimeRawTicks is long g && parentHop.CreationTimeRawTicks is long p
                ? g < p
                : null;
        bool? parentBeforeHost =
            parentHop.CreationTimeRawTicks is long p2 && hostHop.CreationTimeRawTicks is long h
                ? p2 < h
                : null;

        BrowserHostBindingStatus status = BrowserHostBindingStatus.Unresolved;
        BrowserHostBinding? binding = null;

        try
        {
            if (hostImagePath is not null)
            {
                // Self-referential by design: this process IS the "host" Gate 031F6F's resolver was
                // written to identify, so its own already-known image path is also the "expected"
                // path -- there is no separate production PRIVON.exe involved anywhere in E5B.
                var resolver = new BrowserHostBindingResolver(hostImagePath, Environment.SystemDirectory);
                status = resolver.Resolve((uint)hostProcessId, out binding);
            }

            WriteEvidence(
                args, extensionOriginArg, extensionOriginTrailingSlash, parentWindowRaw,
                unexpectedExtraArgs, hostProcessId, hostImagePath, status, binding,
                hostHop, parentHop, grandparentHop, grandparentBeforeParent, parentBeforeHost);
        }
        finally
        {
            binding?.Dispose();
        }
    }

    private static void WriteEvidence(
        string[] rawArgs,
        string? extensionOriginArg,
        bool extensionOriginTrailingSlash,
        string? parentWindowRaw,
        List<string> unexpectedExtraArgs,
        int hostProcessId,
        string? hostImagePath,
        BrowserHostBindingStatus status,
        BrowserHostBinding? binding,
        ProcessHopRecord hostHop,
        ProcessHopRecord parentHop,
        ProcessHopRecord grandparentHop,
        bool? grandparentBeforeParent,
        bool? parentBeforeHost)
    {
        string evidenceDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PRIVON-DEVMEASURE",
            "evidence");
        Directory.CreateDirectory(evidenceDir);

        var record = new
        {
            timestampUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            hostProcessId,
            // OS self-query (Environment.ProcessPath) -- NOT a parse of any argv element. See
            // rawArgs below for the actual, unmodified .NET args array (which does not include
            // argv[0]/the executable path at all).
            hostImagePath,
            rawArgCount = rawArgs.Length,
            rawArgs,
            extensionOriginArg,
            extensionOriginTrailingSlash,
            parentWindowPresent = parentWindowRaw is not null,
            parentWindowValue = parentWindowRaw,
            unexpectedExtraArgs,

            // Unchanged from the first evidence schema (Gate E5B): BrowserHostBindingResolver's own
            // topology/package/signature JUDGMENT for the resolved browser. Field names and meaning
            // retained exactly so the first evidence file (launch-20260902-095834-575-9520.json)
            // remains readable against this schema.
            bindingStatus = status.ToString(),
            browserProcessId = binding?.BrowserProcessId,
            browserProcessName = binding?.BrowserProcessName,
            browserTopology = binding?.Topology.ToString(),
            browserPackageIdentity = binding?.BrowserPackageIdentity.ToString(),
            browserPackageFamilyName = binding?.BrowserPackageFamilyName,
            browserExecutableSignature = binding?.BrowserExecutableSignature.ToString(),
            browserSignerOrganization = binding?.BrowserSignerOrganization,

            // NEW (Gate E5B.1): raw, independently-observed ancestry chain -- see
            // ProcessChainCapture's own doc comment. Never derived from browserTopology above.
            processChain = new
            {
                host = ToJson(hostHop),
                immediateParent = ToJson(parentHop),
                grandparent = ToJson(grandparentHop),
                ordering = new
                {
                    grandparentBeforeParent,
                    parentBeforeHost,
                },
            },
        };

        string fileName = string.Create(
            CultureInfo.InvariantCulture,
            $"launch-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-{hostProcessId}.json");
        string path = Path.Combine(evidenceDir, fileName);

        string json = JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    private static object ToJson(ProcessHopRecord hop) => new
    {
        status = hop.Status.ToString(),
        pid = hop.Pid,
        processName = hop.ProcessName,
        imagePath = hop.ImagePath,
        creationTimeUtc = hop.CreationTimeUtc,
        creationTimeRawTicks = hop.CreationTimeRawTicks,
    };
}
