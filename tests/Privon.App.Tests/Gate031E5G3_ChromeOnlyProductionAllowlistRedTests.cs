using Microsoft.Win32.SafeHandles;
using Privon.Browser;
using Privon.Windows;

namespace Privon.App.Tests;

// PRIVON 0.3.1 Gate E5G.3 -- Chrome-only PRODUCTION WebExtension origin activation. Authorizes
// exactly chrome-extension://aieobgphcpmkfnhadocdhenigmackboo/ in the real, static
// WebExtensionOriginAllowlist.Production -- the ONE production authority NativeMessagingHostInvocation
// .Classify/PrivonEntryPoint.Run consult at process-launch time. Every case below drives real
// production types directly (no reflection needed -- E3/E4/E5G.1C/E5G.2 already made every consulted
// type compile-time-visible). Cases E/F re-prove the SAME two independent gates a real attack would
// have to cross: the launch-time origin classifier (NativeMessagingHostInvocation.Classify against the
// real Production allowlist) and the connection-time E2 browser-binding boundary
// (WebChannelHostServer.AdmitConnectionAsync via WebBrowserGate/ReleaseBrowserSupportPolicy) --
// proving activation of the Chrome origin does not reopen the Chrome-registration -> Edge fallback
// the previous gate (E5G.2) closed.
public class Gate031E5G3_ChromeOnlyProductionAllowlistRedTests
{
    private const string ChromeStoreItemId = "aieobgphcpmkfnhadocdhenigmackboo";
    private const string EdgeCrxId = "fmdcgbjednllpjlogkcjmlocpnpbpljn";
    private const string ExpectedChromeOrigin = "chrome-extension://" + ChromeStoreItemId + "/";
    private const string ExpectedEdgeOrigin = "chrome-extension://" + EdgeCrxId + "/";

    private static readonly SafePipeHandle DummyPipeHandle = new(new IntPtr(-2), ownsHandle: false); // INVALID_HANDLE_VALUE-shaped, never a real handle.

    // ==================================================================
    // A -- Production allowlist exact content.
    // ==================================================================

    [Fact]
    public void A_ProductionAllowlist_ContainsExactlyTheVerifiedChromeOrigin()
    {
        Assert.Single(WebExtensionOriginAllowlist.Production);
        Assert.Contains(ExpectedChromeOrigin, WebExtensionOriginAllowlist.Production);
    }

    // ==================================================================
    // B -- exact Chrome Store origin authorized through the real production classifier/entry point.
    // ==================================================================

    [Fact]
    public void B_RealProductionClassifier_AuthorizesTheExactChromeStoreOrigin()
    {
        var invocation = NativeMessagingHostInvocation.Classify(
            new[] { ExpectedChromeOrigin, "--parent-window=0" }, WebExtensionOriginAllowlist.Production);

        Assert.Equal(NativeMessagingInvocationKind.Host, invocation.Kind);
        Assert.Equal(ExpectedChromeOrigin, invocation.ExtensionOrigin);
    }

    [Fact]
    public void B_RealProductionEntryPoint_DispatchesToHostRunner_ForTheExactChromeStoreOrigin()
    {
        bool normalRan = false, hostRan = false;

        PrivonEntryPoint.Run(
            new[] { ExpectedChromeOrigin, "--parent-window=0" },
            WebExtensionOriginAllowlist.Production,
            normalRunner: () => normalRan = true,
            hostRunner: () => hostRan = true);

        Assert.True(hostRan, "the real Production allowlist must let the verified Chrome Store origin reach host mode.");
        Assert.False(normalRan);
    }

    // ==================================================================
    // C -- Edge Store origin remains unauthorized against the real Production allowlist.
    // ==================================================================

    [Fact]
    public void C_RealProductionAllowlist_RejectsTheEdgeStoreOrigin()
    {
        var invocation = NativeMessagingHostInvocation.Classify(
            new[] { ExpectedEdgeOrigin, "--parent-window=0" }, WebExtensionOriginAllowlist.Production);

        Assert.Equal(NativeMessagingInvocationKind.RejectedHostShaped, invocation.Kind);
    }

    // ==================================================================
    // D -- near-miss origins remain rejected against the real Production allowlist (fail-closed
    // grammar/allowlist preserved -- never broadened to admit the new Store origin).
    // ==================================================================

    public static IEnumerable<object[]> NearMissOrigins() =>
    [
        ["truncated ID (31 chars)", "chrome-extension://aieobgphcpmkfnhadocdhenigmackbo/"],
        ["extra ID character (33 chars)", "chrome-extension://aieobgphcpmkfnhadocdhenigmackbooo/"],
        ["wrong scheme", "chrom-extension://aieobgphcpmkfnhadocdhenigmackboo/"],
        ["unexpected trailing path", "chrome-extension://aieobgphcpmkfnhadocdhenigmackboo/x"],
        ["missing trailing slash", "chrome-extension://aieobgphcpmkfnhadocdhenigmackboo"],
        ["structurally valid but unlisted ID", "chrome-extension://aieobgphcpmkfnhadocdhenigmackboa/"],
    ];

    [Theory]
    [MemberData(nameof(NearMissOrigins))]
    public void D_NearMissOrigins_RemainRejected_AgainstRealProductionAllowlist(string scenario, string origin)
    {
        var invocation = NativeMessagingHostInvocation.Classify(
            new[] { origin, "--parent-window=0" }, WebExtensionOriginAllowlist.Production);

        Assert.True(invocation.Kind == NativeMessagingInvocationKind.RejectedHostShaped,
            $"D ({scenario}): '{origin}' must remain rejected against the real Production allowlist.");
    }

    // ==================================================================
    // E/F -- the origin/browser-support boundary: launch-time origin classification (real Production
    // allowlist) composed with the independent connection-time E2 browser-binding boundary
    // (WebChannelHostServer.AdmitConnectionAsync).
    // ==================================================================

    [Fact]
    public async Task E_ProductionChromeOrigin_WithAuthenticatedChromeBinding_PassesBothBoundaries()
    {
        Assert.Equal(NativeMessagingInvocationKind.Host, NativeMessagingHostInvocation.Classify(
            new[] { ExpectedChromeOrigin, "--parent-window=0" }, WebExtensionOriginAllowlist.Production).Kind);

        var registry = new WebChannelRegistry();
        var manager = new WebChannelManager(registry);
        using var budget = new WebSessionAdmissionBudget(1);
        var binding = new FakeBinding(3131, "chrome", "Google LLC");
        var server = new WebChannelHostServer(
            new NullPipeListener(), _ => 3131, new FakeBindingSource(binding), registry, manager, budget);

        // A never-EOF transport: an admitted session immediately starts reading for Hello, and a
        // MemoryStream with no data would report EOF on the very first read, causing the session to
        // self-close (and dispose the binding) before this test could observe the admitted state.
        using var transport = new BlockingReadStream();
        var lease = await budget.AcquireAsync();
        await server.AdmitConnectionAsync(transport, DummyPipeHandle, lease);

        var live = manager.SnapshotLive();
        try
        {
            Assert.False(binding.Disposed, "E: a supported Chrome binding must not be torn down at this boundary.");
            Assert.Single(live);
        }
        finally
        {
            foreach (var session in live)
                session.Close();
        }
    }

    [Fact]
    public async Task F_ProductionChromeOrigin_WithAuthenticatedEdgeBinding_StillRejectedBeforeHello()
    {
        // The launch-time origin gate now genuinely admits the verified Chrome Store origin --
        // re-proving, against the REAL (non-empty) Production allowlist, that this alone can never
        // admit an Edge-bound connection: the independent E2 binding boundary below must still reject
        // it before any Hello/session admission (the exact fallback E5G.2 closed, now re-checked with
        // Production genuinely non-empty rather than a test-only substitute allowlist).
        Assert.Equal(NativeMessagingInvocationKind.Host, NativeMessagingHostInvocation.Classify(
            new[] { ExpectedChromeOrigin, "--parent-window=0" }, WebExtensionOriginAllowlist.Production).Kind);

        var registry = new WebChannelRegistry();
        var manager = new WebChannelManager(registry);
        using var budget = new WebSessionAdmissionBudget(1);
        var binding = new FakeBinding(3232, "msedge", "Microsoft Corporation");
        var server = new WebChannelHostServer(
            new NullPipeListener(), _ => 3232, new FakeBindingSource(binding), registry, manager, budget);

        using var transport = new MemoryStream();
        var lease = await budget.AcquireAsync();
        await server.AdmitConnectionAsync(transport, DummyPipeHandle, lease);

        Assert.True(binding.Disposed, "F: an authenticated Edge binding must be rejected/disposed before Hello.");
        Assert.Empty(manager.SnapshotLive());
    }

    // ==================================================================
    // G -- test-only allowlist injection remains available and independent of Production: the real
    // classifier still accepts an arbitrary caller-supplied allowlist, so test configuration never
    // collapses into, or leaks from, Production.
    // ==================================================================

    [Fact]
    public void G_TestOnlyAllowlist_IsIndependentOfProduction()
    {
        var testOnlyAllowlist = new HashSet<string>(StringComparer.Ordinal) { ExpectedEdgeOrigin };

        Assert.Equal(NativeMessagingInvocationKind.Host, NativeMessagingHostInvocation.Classify(
            new[] { ExpectedEdgeOrigin, "--parent-window=0" }, testOnlyAllowlist).Kind);

        Assert.Equal(NativeMessagingInvocationKind.RejectedHostShaped, NativeMessagingHostInvocation.Classify(
            new[] { ExpectedEdgeOrigin, "--parent-window=0" }, WebExtensionOriginAllowlist.Production).Kind);

        Assert.DoesNotContain(ExpectedEdgeOrigin, WebExtensionOriginAllowlist.Production);
    }

    // ==================================================================
    // Fakes.
    // ==================================================================

    private sealed class FakeBinding(uint pid, string processName, string signer) : IWebBrowserHostBinding
    {
        public uint BrowserProcessId { get; } = pid;
        public string? BrowserProcessName => processName;
        public PackageIdentityResolution BrowserPackageIdentity => PackageIdentityResolution.NoPackage;
        public ExecutableSignatureResolution BrowserExecutableSignature => ExecutableSignatureResolution.Trusted;
        public string? BrowserSignerOrganization => signer;
        public bool Disposed { get; private set; }
        public RetainedProcessLiveness CheckLiveness() => RetainedProcessLiveness.Alive;
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeBindingSource(IWebBrowserHostBinding binding) : IWebHostBindingSource
    {
        public BrowserHostBindingStatus Resolve(uint hostProcessId, out IWebBrowserHostBinding? resolvedBinding)
        {
            resolvedBinding = binding;
            return BrowserHostBindingStatus.Resolved;
        }
    }

    private sealed class NullPipeListener : IWebPipeListener
    {
        public Task<(Stream Stream, SafePipeHandle Handle)?> AcceptAsync(CancellationToken cancellationToken) =>
            Task.FromResult<(Stream Stream, SafePipeHandle Handle)?>(null);

        public void Dispose()
        {
        }
    }

    /// <summary>A transport whose reads never complete on their own -- unlike an empty
    /// <see cref="MemoryStream"/> (which reports EOF immediately), so an admitted
    /// <see cref="WebChannelSession"/>'s own Hello read stays genuinely pending, exactly like a real,
    /// still-open pipe with no data yet. Disposing it (as <see cref="WebChannelSession.Close"/> does)
    /// faults the pending read with <see cref="ObjectDisposedException"/>, mirroring real pipe
    /// disposal.</summary>
    private sealed class BlockingReadStream : Stream
    {
        private readonly TaskCompletionSource<int> _pendingRead = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, default).GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _pendingRead.Task;
        public override void Write(byte[] buffer, int offset, int count)
        {
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _pendingRead.TrySetException(new ObjectDisposedException(nameof(BlockingReadStream)));
            base.Dispose(disposing);
        }
    }
}
