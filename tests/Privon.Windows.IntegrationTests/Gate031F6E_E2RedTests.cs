using System.Reflection;
using Privon.Windows;

namespace Privon.Windows.IntegrationTests;

// PRIVON 0.3.1 Gate 031F6F -- Phase E2 GREEN behavioral suite for Win32 browser-host process
// binding + retained browser-process ownership/liveness + mechanical signature/signer fact
// extraction (Privon.Windows). Originally written RED (Gate 031F6E, none of E2's production types
// existed yet); Gate 031F6F implemented BrowserHostBindingStatus/BrowserHostTopology/
// RetainedProcessLiveness/RetainedProcess/BrowserHostBinding/BrowserHostBindingResolver/
// IProcessTopologySource/Win32ProcessTopologySource, turning every test below into REAL behavior
// against the REAL production BrowserHostBindingResolver, driven by the deterministic, OS-free
// ScriptedProcessTopologySource double (implementing the real IProcessTopologySource).
//
// E3 IS OUT OF SCOPE: no NamedPipeServerStream/Client, no GetNamedPipeClientProcessId, no host argv,
// no Hello/HelloAck transport, no WebChannelManager/Registry connect behavior, no extension, no
// registration. This file stops exactly at "a mechanical browser-host binding was resolved (or
// fail-closed) and can be disposed" -- never at "the channel was admitted."
public class Gate031F6E_E2RedTests
{
    private const string ExpectedHostPath = @"C:\Program Files\Vendor\host.exe";
    private const string SystemDirectory = @"C:\Windows\System32";
    private const string ExactCmdPath = @"C:\Windows\System32\cmd.exe";
    private const string BrowserPath = @"C:\Program Files\BrowserCo\browser.exe";
    private const string BrowserSigner = "Browser Vendor Inc.";

    private static BrowserHostBindingResolver CreateResolver(
        ScriptedProcessTopologySource source, string? expectedHostPath = null, string? systemDirectory = null) =>
        new(expectedHostPath ?? ExpectedHostPath, systemDirectory ?? SystemDirectory, source);

    private static ScriptedProcessTopologySource.ProcessNodeScript TrustedBrowserNode(long creationTicks) => new()
    {
        ImagePath = BrowserPath,
        CreationTimeTicks = creationTicks,
        PackageIdentity = PackageIdentityResolution.NoPackage,
        SignatureResult = ExecutableSignatureResolution.Trusted,
        SignerOrganization = BrowserSigner,
    };

    // ==================================================================
    // R1 -- DIRECT topology success.
    // ==================================================================
    [Fact]
    public void R1_DirectTopology_BrowserToHost_ResolvesToTheBrowserPid()
    {
        var source = new ScriptedProcessTopologySource();
        source.AddNode(500, new() { ImagePath = ExpectedHostPath, CreationTimeTicks = 300, ParentProcessId = 100 });
        source.AddNode(100, TrustedBrowserNode(100));

        var status = CreateResolver(source).Resolve(500, out var binding);

        Assert.Equal(BrowserHostBindingStatus.Resolved, status);
        Assert.NotNull(binding);
        Assert.Equal(BrowserHostTopology.Direct, binding!.Topology);
        Assert.Equal(100u, binding.BrowserProcessId);
        Assert.Equal("browser", binding.BrowserProcessName);
        Assert.Equal(ExecutableSignatureResolution.Trusted, binding.BrowserExecutableSignature);
        Assert.Equal(BrowserSigner, binding.BrowserSignerOrganization);

        Assert.True(source.IssuedInspectionHandles[500].IsClosed, "host temporary handle must be closed.");
        Assert.False(source.IssuedRetentionHandles[100].IsClosed, "browser retained handle must remain open.");

        binding.Dispose();
        Assert.True(source.IssuedRetentionHandles[100].IsClosed, "browser handle must close once the binding is disposed.");
    }

    // ==================================================================
    // R2 -- CommandIntermediary topology success.
    // ==================================================================
    [Fact]
    public void R2_CommandIntermediaryTopology_BrowserToCmdToHost_ResolvesToTheGrandparentPid()
    {
        var source = new ScriptedProcessTopologySource();
        source.AddNode(500, new() { ImagePath = ExpectedHostPath, CreationTimeTicks = 300, ParentProcessId = 250 });
        source.AddNode(250, new() { ImagePath = ExactCmdPath, CreationTimeTicks = 200, ParentProcessId = 100 });
        source.AddNode(100, TrustedBrowserNode(100));

        var status = CreateResolver(source).Resolve(500, out var binding);

        Assert.Equal(BrowserHostBindingStatus.Resolved, status);
        Assert.NotNull(binding);
        Assert.Equal(BrowserHostTopology.CommandIntermediary, binding!.Topology);
        Assert.Equal(100u, binding.BrowserProcessId);

        Assert.True(source.IssuedInspectionHandles[500].IsClosed, "host handle must be closed.");
        Assert.True(source.IssuedRetentionHandles[250].IsClosed, "cmd temporary retention handle must be closed.");
        Assert.False(source.IssuedRetentionHandles[100].IsClosed, "browser retained handle must remain open.");

        binding.Dispose();
    }

    // ==================================================================
    // R3 -- structurally bounded depth: no recursion, no ancestor loop, no depth configuration.
    // ==================================================================
    [Fact]
    public void R3_DepthOfThreeViaTwoCmdHops_IsRejected_NeverRecursivelyResolved()
    {
        var source = new ScriptedProcessTopologySource();
        source.AddNode(500, new() { ImagePath = ExpectedHostPath, CreationTimeTicks = 400, ParentProcessId = 300 });
        source.AddNode(300, new() { ImagePath = ExactCmdPath, CreationTimeTicks = 300, ParentProcessId = 200 });
        source.AddNode(200, new() { ImagePath = ExactCmdPath, CreationTimeTicks = 200, ParentProcessId = 100 });
        source.AddNode(100, TrustedBrowserNode(100));

        var status = CreateResolver(source).Resolve(500, out var binding);

        Assert.Equal(BrowserHostBindingStatus.UnknownIntermediary, status);
        Assert.Null(binding);
        // The resolver examines exactly parent (300) and grandparent (200, itself found to be cmd) --
        // it never looks one hop further for PID 100, proving there is no recursion/depth beyond two.
        Assert.DoesNotContain(100u, source.IssuedInspectionHandles.Keys.Concat(source.IssuedRetentionHandles.Keys));
    }

    [Fact]
    public void R3_SecondCmdAtBrowserCandidatePosition_IsRejectedAsUnknownIntermediary()
    {
        var source = new ScriptedProcessTopologySource();
        source.AddNode(500, new() { ImagePath = ExpectedHostPath, CreationTimeTicks = 300, ParentProcessId = 250 });
        source.AddNode(250, new() { ImagePath = ExactCmdPath, CreationTimeTicks = 200, ParentProcessId = 100 });
        // The grandparent (the position a real browser must occupy) is itself another cmd.exe.
        source.AddNode(100, new() { ImagePath = ExactCmdPath, CreationTimeTicks = 100 });

        var status = CreateResolver(source).Resolve(500, out var binding);

        Assert.Equal(BrowserHostBindingStatus.UnknownIntermediary, status);
        Assert.Null(binding);
        Assert.True(source.IssuedRetentionHandles[250].IsClosed);
        Assert.True(source.IssuedRetentionHandles[100].IsClosed);
    }

    // ==================================================================
    // R4 -- arbitrary immediate-parent intermediaries rejected (neither browser candidate nor exact
    // System32 cmd). Deliberately mechanical/arbitrary names -- no browser product name is frozen in
    // Privon.Windows.
    // ==================================================================
    [Theory]
    [InlineData(@"C:\Windows\System32\conhost.exe")]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe")]
    [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe")]
    [InlineData(@"C:\Windows\System32\wscript.exe")]
    [InlineData(@"C:\Windows\System32\cscript.exe")]
    public void R4_ArbitraryImmediateParent_IsNeitherBrowserCandidateNorCmdIntermediary_TreatedAsDirectBrowserCandidateAndFailsClosedOnIdentity(
        string immediateParentImagePath)
    {
        // An arbitrary non-cmd immediate parent is mechanically indistinguishable from a genuine
        // DIRECT browser candidate at the topology level (Privon.Windows knows no browser product
        // names) -- it is opened as the browser candidate and its own mechanical facts are truthfully
        // extracted. Rejecting it as "not really a browser" is exclusively WebBrowserGate's job
        // (App-side policy) -- see Gate031F6E_WebBrowserGateRedTests.cs R17. This test proves the
        // NEGATIVE half that belongs to Privon.Windows: it is never misclassified as the cmd
        // intermediary and never granted deeper (grandparent) traversal.
        var source = new ScriptedProcessTopologySource();
        source.AddNode(500, new() { ImagePath = ExpectedHostPath, CreationTimeTicks = 200, ParentProcessId = 100 });
        source.AddNode(100, new() { ImagePath = immediateParentImagePath, CreationTimeTicks = 100 });

        var status = CreateResolver(source).Resolve(500, out var binding);

        Assert.Equal(BrowserHostBindingStatus.Resolved, status);
        Assert.NotNull(binding);
        Assert.Equal(BrowserHostTopology.Direct, binding!.Topology);
        Assert.Equal(100u, binding.BrowserProcessId);
        binding.Dispose();
    }

    // ==================================================================
    // R5 -- System32 cmd path validation is full-path, OrdinalIgnoreCase; every spoof rejected (a
    // spoofed cmd is mechanically just an ordinary DIRECT browser candidate whose facts are honestly
    // extracted, exactly like R4 -- the resolver never special-cases "looks like cmd.exe").
    // ==================================================================
    [Theory]
    [InlineData("cmd.exe", "basename-only cmd.exe (no directory at all)")]
    [InlineData(@"C:\Windows\SysWOW64\cmd.exe", "SysWOW64 cmd.exe (wrong system directory)")]
    [InlineData(@"C:\Users\Public\cmd.exe", "user-writable directory cmd.exe")]
    [InlineData(@"System32\cmd.exe", "relative cmd.exe path")]
    [InlineData(@"C:\Windows\System32\cmd_evil.exe", "renamed copy in the real System32 directory")]
    [InlineData(@"C:\Temp\System32\cmd.exe", "different, non-Windows System32-shaped directory")]
    public void R5_CmdPathSpoof_NeverMatchesTheExactSystem32Path_TreatedAsDirectBrowserCandidate(string spoofedCmdPath, string scenario)
    {
        var source = new ScriptedProcessTopologySource();
        source.AddNode(500, new() { ImagePath = ExpectedHostPath, CreationTimeTicks = 300, ParentProcessId = 100 });
        source.AddNode(100, new() { ImagePath = spoofedCmdPath, CreationTimeTicks = 100 });

        var status = CreateResolver(source).Resolve(500, out var binding);

        Assert.Equal(BrowserHostBindingStatus.Resolved, status);
        Assert.Equal(BrowserHostTopology.Direct, binding!.Topology);
        Assert.False(source.IssuedRetentionHandles.ContainsKey(250), $"spoof ({scenario}) must never be treated as a cmd intermediary.");
        binding.Dispose();
    }

    [Fact]
    public void R5_CmdPathCaseVariation_OfTheExactInjectedSystem32Path_IsAcceptedAsCommandIntermediary()
    {
        var source = new ScriptedProcessTopologySource();
        source.AddNode(500, new() { ImagePath = ExpectedHostPath, CreationTimeTicks = 300, ParentProcessId = 250 });
        source.AddNode(250, new() { ImagePath = @"C:\WINDOWS\system32\CMD.EXE", CreationTimeTicks = 200, ParentProcessId = 100 });
        source.AddNode(100, TrustedBrowserNode(100));

        var status = CreateResolver(source).Resolve(500, out var binding);

        Assert.Equal(BrowserHostBindingStatus.Resolved, status);
        Assert.Equal(BrowserHostTopology.CommandIntermediary, binding!.Topology);
        Assert.Equal(100u, binding.BrowserProcessId);
        binding.Dispose();
    }

    // ==================================================================
    // R6 -- single-source PID rule.
    // ==================================================================
    [Fact]
    public void R6_BrowserHostBinding_HasNoPublicConstructor_NoWayToSupplyAnIndependentPid()
    {
        var bindingType = typeof(BrowserHostBinding);
        Assert.Empty(bindingType.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public void R6_BrowserProcessId_AlwaysEqualsTheOwnedRetainedProcessId_AcrossBothTopologies()
    {
        var directSource = new ScriptedProcessTopologySource();
        directSource.AddNode(500, new() { ImagePath = ExpectedHostPath, CreationTimeTicks = 300, ParentProcessId = 100 });
        directSource.AddNode(100, TrustedBrowserNode(100));
        CreateResolver(directSource).Resolve(500, out var directBinding);
        Assert.Equal(directBinding!.BrowserProcess.ProcessId, directBinding.BrowserProcessId);
        directBinding.Dispose();

        var cmdSource = new ScriptedProcessTopologySource();
        cmdSource.AddNode(500, new() { ImagePath = ExpectedHostPath, CreationTimeTicks = 300, ParentProcessId = 250 });
        cmdSource.AddNode(250, new() { ImagePath = ExactCmdPath, CreationTimeTicks = 200, ParentProcessId = 100 });
        cmdSource.AddNode(100, TrustedBrowserNode(100));
        CreateResolver(cmdSource).Resolve(500, out var cmdBinding);
        Assert.Equal(cmdBinding!.BrowserProcess.ProcessId, cmdBinding.BrowserProcessId);
        cmdBinding.Dispose();
    }

    // ==================================================================
    // R7 -- strict creation ordering, table-driven, no tolerance.
    // ==================================================================
    public static IEnumerable<object[]> CreationOrderingViolations() =>
    [
        ["CMD equal (cmd==host)", 100L, 1000L, 1000L],
        ["CMD reversed (cmd>host)", 100L, 1200L, 1000L],
        ["CMD equal (browser==cmd)", 500L, 500L, 1000L],
        ["CMD reversed (browser>cmd)", 600L, 500L, 1000L],
    ];

    [Theory]
    [MemberData(nameof(CreationOrderingViolations))]
    public void R7_CmdCreationOrderingViolation_FailsClosed_NoToleranceNoEpsilon(
        string scenario, long browserTicks, long cmdTicks, long hostTicks)
    {
        var source = new ScriptedProcessTopologySource();
        source.AddNode(500, new() { ImagePath = ExpectedHostPath, CreationTimeTicks = hostTicks, ParentProcessId = 250 });
        source.AddNode(250, new() { ImagePath = ExactCmdPath, CreationTimeTicks = cmdTicks, ParentProcessId = 100 });
        source.AddNode(100, TrustedBrowserNode(browserTicks));

        var status = CreateResolver(source).Resolve(500, out var binding);

        Assert.True(status == BrowserHostBindingStatus.CreationOrderingViolation, $"{scenario}: expected CreationOrderingViolation, got {status}.");
        Assert.Null(binding);
    }

    [Theory]
    [InlineData(100L, 100L)] // equal
    [InlineData(200L, 100L)] // reversed
    public void R7_DirectCreationOrderingViolation_FailsClosed(long browserTicks, long hostTicks)
    {
        var source = new ScriptedProcessTopologySource();
        source.AddNode(500, new() { ImagePath = ExpectedHostPath, CreationTimeTicks = hostTicks, ParentProcessId = 100 });
        source.AddNode(100, TrustedBrowserNode(browserTicks));

        var status = CreateResolver(source).Resolve(500, out var binding);

        Assert.Equal(BrowserHostBindingStatus.CreationOrderingViolation, status);
        Assert.Null(binding);
    }

    [Fact]
    public void R7_HostCreationTimeUnavailable_ReturnsDedicatedStatus_NotGenericOrderingViolation()
    {
        var source = new ScriptedProcessTopologySource();
        source.AddNode(500, new() { ImagePath = ExpectedHostPath, CreationTimeTicks = null, ParentProcessId = 100 });
        source.AddNode(100, TrustedBrowserNode(100));

        var status = CreateResolver(source).Resolve(500, out var binding);

        Assert.Equal(BrowserHostBindingStatus.HostCreationTimeUnavailable, status);
        Assert.Null(binding);
        Assert.False(source.CallLog.Any(c => c.StartsWith("GetParentProcessId")), "must fail before even querying the parent.");
    }

    [Fact]
    public void R7_CmdCreationTimeUnavailable_IsAnOrderingViolation()
    {
        var source = new ScriptedProcessTopologySource();
        source.AddNode(500, new() { ImagePath = ExpectedHostPath, CreationTimeTicks = 1000, ParentProcessId = 250 });
        source.AddNode(250, new() { ImagePath = ExactCmdPath, CreationTimeTicks = null, ParentProcessId = 100 });
        source.AddNode(100, TrustedBrowserNode(100));

        var status = CreateResolver(source).Resolve(500, out var binding);

        Assert.Equal(BrowserHostBindingStatus.CreationOrderingViolation, status);
        Assert.Null(binding);
    }

    // ==================================================================
    // R8 -- zero / unresolved PIDs and query failures fail closed, never throw.
    // ==================================================================
    [Fact]
    public void R8_HostPidZero_FailsClosed()
    {
        var source = new ScriptedProcessTopologySource();
        var status = CreateResolver(source).Resolve(0, out var binding);
        Assert.Equal(BrowserHostBindingStatus.HostProcessIdInvalid, status);
        Assert.Null(binding);
    }

    [Fact]
    public void R8_DirectImmediateParentPidZero_FailsClosed()
    {
        var source = new ScriptedProcessTopologySource();
        source.AddNode(500, new() { ImagePath = ExpectedHostPath, CreationTimeTicks = 200, ParentProcessId = 0 });
        var status = CreateResolver(source).Resolve(500, out var binding);
        Assert.Equal(BrowserHostBindingStatus.ParentProcessIdInvalid, status);
        Assert.Null(binding);
    }

    [Fact]
    public void R8_CmdGrandparentPidZero_FailsClosed()
    {
        var source = new ScriptedProcessTopologySource();
        source.AddNode(500, new() { ImagePath = ExpectedHostPath, CreationTimeTicks = 300, ParentProcessId = 250 });
        source.AddNode(250, new() { ImagePath = ExactCmdPath, CreationTimeTicks = 200, ParentProcessId = 0 });
        var status = CreateResolver(source).Resolve(500, out var binding);
        Assert.Equal(BrowserHostBindingStatus.GrandparentProcessIdInvalid, status);
        Assert.Null(binding);
    }

    [Fact]
    public void R8_ParentQueryUnavailable_FailsClosed_NeverThrows()
    {
        var source = new ScriptedProcessTopologySource();
        source.AddNode(500, new() { ImagePath = ExpectedHostPath, CreationTimeTicks = 200, ParentQuerySucceeds = false });
        var status = CreateResolver(source).Resolve(500, out var binding);
        Assert.Equal(BrowserHostBindingStatus.ParentProcessIdUnavailable, status);
        Assert.Null(binding);
    }

    [Fact]
    public void R8_GrandparentQueryUnavailable_FailsClosed_NeverThrows()
    {
        var source = new ScriptedProcessTopologySource();
        source.AddNode(500, new() { ImagePath = ExpectedHostPath, CreationTimeTicks = 300, ParentProcessId = 250 });
        source.AddNode(250, new() { ImagePath = ExactCmdPath, CreationTimeTicks = 200, ParentQuerySucceeds = false });
        var status = CreateResolver(source).Resolve(500, out var binding);
        Assert.Equal(BrowserHostBindingStatus.GrandparentProcessIdUnavailable, status);
        Assert.Null(binding);
    }

    [Fact]
    public void R8_HostOpenForInspectionFails_FailsClosed_NeverThrows()
    {
        var source = new ScriptedProcessTopologySource();
        source.AddNode(500, new() { ImagePath = ExpectedHostPath, OpenForInspectionSucceeds = false });
        var status = CreateResolver(source).Resolve(500, out var binding);
        Assert.Equal(BrowserHostBindingStatus.HostOpenFailed, status);
        Assert.Null(binding);
    }

    [Fact]
    public void R8_BrowserOpenForRetentionFails_FailsClosed_NeverThrows()
    {
        var source = new ScriptedProcessTopologySource();
        source.AddNode(500, new() { ImagePath = ExpectedHostPath, CreationTimeTicks = 200, ParentProcessId = 100 });
        source.AddNode(100, new() { ImagePath = BrowserPath, CreationTimeTicks = 100, OpenForRetentionSucceeds = false });
        var status = CreateResolver(source).Resolve(500, out var binding);
        Assert.Equal(BrowserHostBindingStatus.ParentOpenFailed, status);
        Assert.Null(binding);
    }

    // ==================================================================
    // R9 -- host executable path binding: Path.GetFullPath + OrdinalIgnoreCase.
    // ==================================================================
    [Theory]
    [InlineData(@"C:\Program Files\Vendor\host.exe", true, "exact match")]
    [InlineData(@"C:\PROGRAM FILES\VENDOR\HOST.EXE", true, "case-only variation")]
    [InlineData(@"C:\Program Files\Other\different.exe", false, "different path entirely")]
    public void R9_HostExecutablePathComparison_IsFullPathOrdinalIgnoreCase(
        string actualHostImagePath, bool shouldAccept, string scenario)
    {
        var source = new ScriptedProcessTopologySource();
        source.AddNode(500, new() { ImagePath = actualHostImagePath, CreationTimeTicks = 200, ParentProcessId = 100 });
        source.AddNode(100, TrustedBrowserNode(100));

        var status = CreateResolver(source).Resolve(500, out var binding);

        if (shouldAccept)
        {
            Assert.True(status == BrowserHostBindingStatus.Resolved, $"{scenario}: expected acceptance, got {status}.");
            binding!.Dispose();
        }
        else
        {
            Assert.Equal(BrowserHostBindingStatus.HostImagePathMismatch, status);
            Assert.Null(binding);
        }
    }

    [Fact]
    public void R9_HostImagePathUnavailable_IsRejected()
    {
        var source = new ScriptedProcessTopologySource();
        source.AddNode(500, new() { ImagePath = null, CreationTimeTicks = 200, ParentProcessId = 100 });
        var status = CreateResolver(source).Resolve(500, out var binding);
        Assert.Equal(BrowserHostBindingStatus.HostImagePathUnavailable, status);
        Assert.Null(binding);
    }

    [Theory]
    [InlineData(null, SystemDirectory, typeof(ArgumentNullException))]
    [InlineData(ExpectedHostPath, null, typeof(ArgumentNullException))]
    [InlineData("", SystemDirectory, typeof(ArgumentException))]
    [InlineData(ExpectedHostPath, "", typeof(ArgumentException))]
    [InlineData("relative\\host.exe", SystemDirectory, typeof(ArgumentException))]
    [InlineData(ExpectedHostPath, "relative\\System32", typeof(ArgumentException))]
    public void R9_ConstructorWiringErrors_NullEmptyOrRelativePaths_ThrowByProgrammerErrorContract(
        string? expectedHostPath, string? systemDirectory, Type expectedExceptionType)
    {
        var source = new ScriptedProcessTopologySource();
        Assert.Throws(expectedExceptionType, () => new BrowserHostBindingResolver(expectedHostPath!, systemDirectory!, source));
    }

    // ==================================================================
    // R10 -- mechanical facts + structural boundary (BrowserHostBinding carries only frozen facts).
    // ==================================================================
    [Fact]
    public void R10_BrowserHostBinding_CarriesOnlyTheFrozenMechanicalFacts()
    {
        var memberNames = typeof(BrowserHostBinding)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToList();

        string[] forbiddenSubstrings =
        [
            "Origin", "ChannelId", "Revision", "Epoch", "SupportedWebTarget", "Authoriz",
            "HostProcessId", "HostPid", "CmdProcessId", "CmdPid", "CmdHandle", "Certificate",
            "WindowHandle", "Hwnd",
        ];

        var offending = memberNames
            .Where(n => forbiddenSubstrings.Any(f => n.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(offending);

        string[] requiredSubstrings =
        [
            "BrowserProcessName", "BrowserPackageIdentity", "BrowserPackageFamilyName",
            "BrowserExecutableSignature", "BrowserSignerOrganization", "Topology", "BrowserProcess",
        ];
        foreach (string required in requiredSubstrings)
            Assert.Contains(required, memberNames);
    }

    // ==================================================================
    // R11 -- retained ownership: sealed IDisposable, no public raw handle surface.
    // ==================================================================
    [Fact]
    public void R11_RetainedProcess_IsSealedDisposable_WithNoPublicRawHandleSurface()
    {
        var type = typeof(RetainedProcess);

        Assert.True(type.IsSealed, "RetainedProcess must be sealed.");
        Assert.Contains(typeof(IDisposable), type.GetInterfaces());

        var publicMembers = type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        var rawHandleLeaks = publicMembers
            .Where(m => m is PropertyInfo or FieldInfo)
            .Where(m =>
            {
                var memberType = m is PropertyInfo p ? p.PropertyType : ((FieldInfo)m).FieldType;
                return memberType == typeof(nint) || memberType == typeof(IntPtr)
                    || memberType.Name.Contains("SafeProcessHandle", StringComparison.Ordinal)
                    || memberType.Name.Contains("SafeHandle", StringComparison.Ordinal);
            })
            .ToList();
        Assert.Empty(rawHandleLeaks);

        Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
    }

    // ==================================================================
    // R12 -- failure cleanup: every non-Resolved status leaves binding null with every handle
    // disposed.
    // ==================================================================
    [Fact]
    public void R12_HostOpenFailed_NoHandlesLeftOpen()
    {
        var source = new ScriptedProcessTopologySource();
        source.AddNode(500, new() { OpenForInspectionSucceeds = false });
        var status = CreateResolver(source).Resolve(500, out var binding);
        Assert.Equal(BrowserHostBindingStatus.HostOpenFailed, status);
        Assert.Null(binding);
        Assert.Empty(source.IssuedInspectionHandles);
        Assert.Empty(source.IssuedRetentionHandles);
    }

    [Fact]
    public void R12_HostImagePathMismatch_HostHandleDisposed()
    {
        var source = new ScriptedProcessTopologySource();
        source.AddNode(500, new() { ImagePath = @"C:\Other\different.exe", CreationTimeTicks = 200, ParentProcessId = 100 });
        var status = CreateResolver(source).Resolve(500, out var binding);
        Assert.Equal(BrowserHostBindingStatus.HostImagePathMismatch, status);
        Assert.Null(binding);
        Assert.True(source.IssuedInspectionHandles[500].IsClosed);
    }

    [Fact]
    public void R12_CmdSpoofDetected_AllTemporaryHandlesDisposed()
    {
        var source = new ScriptedProcessTopologySource();
        source.AddNode(500, new() { ImagePath = ExpectedHostPath, CreationTimeTicks = 300, ParentProcessId = 100 });
        source.AddNode(100, new() { ImagePath = "cmd.exe", CreationTimeTicks = 100 }); // basename spoof -> treated as Direct browser candidate
        var status = CreateResolver(source).Resolve(500, out var binding);
        Assert.Equal(BrowserHostBindingStatus.Resolved, status); // proves R5: spoof is never granted cmd treatment
        Assert.True(source.IssuedInspectionHandles[500].IsClosed);
        binding!.Dispose();
    }

    [Fact]
    public void R12_DepthExceeded_AllAcquiredHandlesDisposed()
    {
        var source = new ScriptedProcessTopologySource();
        source.AddNode(500, new() { ImagePath = ExpectedHostPath, CreationTimeTicks = 400, ParentProcessId = 300 });
        source.AddNode(300, new() { ImagePath = ExactCmdPath, CreationTimeTicks = 300, ParentProcessId = 200 });
        source.AddNode(200, new() { ImagePath = ExactCmdPath, CreationTimeTicks = 200, ParentProcessId = 100 });
        source.AddNode(100, TrustedBrowserNode(100));

        var status = CreateResolver(source).Resolve(500, out var binding);

        Assert.Equal(BrowserHostBindingStatus.UnknownIntermediary, status);
        Assert.Null(binding);
        Assert.True(source.IssuedInspectionHandles[500].IsClosed);
        Assert.True(source.IssuedRetentionHandles[300].IsClosed);
        Assert.True(source.IssuedRetentionHandles[200].IsClosed);
    }

    [Fact]
    public void R12_CreationOrderingViolation_AllAcquiredHandlesDisposed()
    {
        var source = new ScriptedProcessTopologySource();
        source.AddNode(500, new() { ImagePath = ExpectedHostPath, CreationTimeTicks = 100, ParentProcessId = 100 });
        source.AddNode(100, TrustedBrowserNode(200)); // reversed

        var status = CreateResolver(source).Resolve(500, out var binding);

        Assert.Equal(BrowserHostBindingStatus.CreationOrderingViolation, status);
        Assert.Null(binding);
        Assert.True(source.IssuedInspectionHandles[500].IsClosed);
        Assert.True(source.IssuedRetentionHandles[100].IsClosed);
    }

    // ==================================================================
    // R13 -- already-exited selected browser.
    // ==================================================================
    [Fact]
    public void R13_SelectedBrowserAlreadyExitedAtResolutionTime_ReturnsBrowserAlreadyExited()
    {
        var source = new ScriptedProcessTopologySource();
        source.AddNode(500, new() { ImagePath = ExpectedHostPath, CreationTimeTicks = 200, ParentProcessId = 100 });
        source.AddNode(100, new()
        {
            ImagePath = BrowserPath, CreationTimeTicks = 100, Liveness = "Exited",
        });

        var status = CreateResolver(source).Resolve(500, out var binding);

        Assert.Equal(BrowserHostBindingStatus.BrowserAlreadyExited, status);
        Assert.Null(binding);
        Assert.True(source.IssuedRetentionHandles[100].IsClosed, "the already-exited browser handle must be disposed, never left open.");
    }

    // ==================================================================
    // R14 -- RetainedProcess.CheckLiveness tri-state semantics, exercised directly (independent of
    // the resolver's own flow).
    // ==================================================================
    [Theory]
    [InlineData("Alive", RetainedProcessLiveness.Alive)]
    [InlineData("Exited", RetainedProcessLiveness.Exited)]
    [InlineData("Unavailable", RetainedProcessLiveness.Unavailable)]
    public void R14_CheckLiveness_TriStateSemantics(string scriptedLiveness, RetainedProcessLiveness expected)
    {
        var source = new ScriptedProcessTopologySource();
        source.AddNode(100, new() { Liveness = scriptedLiveness });
        source.TryOpenForRetention(100, out var handle);

        var retained = new RetainedProcess(handle!, 100, source);

        Assert.Equal(expected, retained.CheckLiveness());
        retained.Dispose();
    }

    [Fact]
    public void R14_CheckLiveness_AfterDispose_ReportsUnavailable_NoPidReopen()
    {
        var source = new ScriptedProcessTopologySource();
        source.AddNode(100, new() { Liveness = "Alive" });
        source.TryOpenForRetention(100, out var handle);
        var retained = new RetainedProcess(handle!, 100, source);

        retained.Dispose();
        int callsBeforeCheck = source.CallLog.Count;
        var liveness = retained.CheckLiveness();

        Assert.Equal(RetainedProcessLiveness.Unavailable, liveness);
        Assert.Equal(callsBeforeCheck, source.CallLog.Count); // no additional native call -- no PID reopen.
    }

    // ==================================================================
    // R15 -- Windows policy hygiene.
    // ==================================================================
    [Fact]
    public void R15_Control_PrivonWindowsProject_RetainsZeroProjectReference()
    {
        string? path = TryFindRepositoryFile(Path.Combine("src", "Privon.Windows", "Privon.Windows.csproj"));
        Assert.True(path is not null, "Could not locate src/Privon.Windows/Privon.Windows.csproj via the repository-root walk.");

        string csproj = File.ReadAllText(path!);
        Assert.DoesNotContain("<ProjectReference", csproj, StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryFindRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PRIVON.slnx")))
            directory = directory.Parent;

        if (directory is null)
            return null;

        string candidate = Path.Combine(directory.FullName, relativePath);
        return File.Exists(candidate) ? candidate : null;
    }

    private static readonly string[] ForbiddenWindowsE2Tokens =
    [
        "chrome", "msedge", "Google LLC", "Microsoft Corporation", "SupportedWebTarget", "WebTargetGate",
        "NamedPipe", "Registry.", "HKEY_", "Clipboard",
        "CreateToolhelp32Snapshot", "PROCESSENTRY32", "Process.GetProcesses", "System.Diagnostics.Process",
    ];

    [Theory]
    [InlineData("BrowserHostBindingResolver")]
    [InlineData("Win32ProcessTopologySource")]
    [InlineData("BrowserHostBinding")]
    public void R15_SourceHygiene_NewE2WindowsFiles_ContainNoProductPolicyOrProhibitedApiTokens(string typeSimpleName)
    {
        string? path = TryFindRepositoryFile(Path.Combine("src", "Privon.Windows", typeSimpleName + ".cs"));
        Assert.True(path is not null, $"src/Privon.Windows/{typeSimpleName}.cs must exist by E2 GREEN.");

        string source = File.ReadAllText(path!);
        var hits = ForbiddenWindowsE2Tokens.Where(t => source.Contains(t, StringComparison.Ordinal)).ToList();
        Assert.Empty(hits);
    }

    // ==================================================================
    // R16 -- signature facts + package signature-inspection gate.
    // ==================================================================
    [Theory]
    [InlineData(PackageIdentityResolution.NoPackage, ExecutableSignatureResolution.Trusted, "Browser Vendor Inc.", true)]
    [InlineData(PackageIdentityResolution.NoPackage, ExecutableSignatureResolution.Untrusted, null, true)]
    [InlineData(PackageIdentityResolution.NoPackage, ExecutableSignatureResolution.Unresolved, null, true)]
    [InlineData(PackageIdentityResolution.Resolved, ExecutableSignatureResolution.NotInspected, null, false)]
    public void R16_SignatureInspectionGate_OnlyRunsForNoPackage_FactsReturnedFaithfully(
        PackageIdentityResolution packageIdentity, ExecutableSignatureResolution scriptedSignature,
        string? scriptedSigner, bool inspectionShouldRun)
    {
        var source = new ScriptedProcessTopologySource();
        source.AddNode(500, new() { ImagePath = ExpectedHostPath, CreationTimeTicks = 200, ParentProcessId = 100 });
        source.AddNode(100, new()
        {
            ImagePath = BrowserPath, CreationTimeTicks = 100,
            PackageIdentity = packageIdentity, SignatureResult = scriptedSignature, SignerOrganization = scriptedSigner,
        });

        var status = CreateResolver(source).Resolve(500, out var binding);

        Assert.Equal(BrowserHostBindingStatus.Resolved, status);
        Assert.Equal(inspectionShouldRun ? 1 : 0, source.SignatureResolutionCallCount);

        if (inspectionShouldRun)
        {
            Assert.Equal(scriptedSignature, binding!.BrowserExecutableSignature);
            Assert.Equal(scriptedSigner, binding.BrowserSignerOrganization);
        }
        else
        {
            Assert.Equal(ExecutableSignatureResolution.NotInspected, binding!.BrowserExecutableSignature);
            Assert.Null(binding.BrowserSignerOrganization);
        }

        binding.Dispose();
    }

    [Fact]
    public void R16_HostAndCmdIntermediary_AreNeverSignatureVerified()
    {
        var source = new ScriptedProcessTopologySource();
        source.AddNode(500, new() { ImagePath = ExpectedHostPath, CreationTimeTicks = 300, ParentProcessId = 250 });
        source.AddNode(250, new() { ImagePath = ExactCmdPath, CreationTimeTicks = 200, ParentProcessId = 100 });
        source.AddNode(100, TrustedBrowserNode(100));

        var status = CreateResolver(source).Resolve(500, out var binding);

        Assert.Equal(BrowserHostBindingStatus.Resolved, status);
        Assert.Equal([100u], source.SignatureResolvedPids);
        binding!.Dispose();
    }
}
