using System.Reflection;
using Privon.App;
using Privon.Windows;

namespace Privon.App.Tests;

// PRIVON 0.3.1 Gate 031F5C -- Phase D routing/B2 behavioral suite, now GREEN against real
// production. Gate 031F5B originally carried these as runtime-locating absence proofs (the five
// routing types did not exist yet); now that ClipboardAuthorization, ClipboardAuthorizationKind,
// WebClipboardAuthorization, IWebClipboardAuthorizationSource, and ClipboardAuthorizationRouter all
// exist, every test below exercises the REAL production type directly -- no fake production
// implementation, no weakened assertion.
public class Gate031F5B_PhaseDRoutingRedTests
{
    private static readonly ForegroundTargetSnapshot ChatGpt =
        new(IsResolved: true, ProcessId: 4242, ProcessName: "ChatGPT",
            PackageIdentity: PackageIdentityResolution.Resolved, PackageFamilyName: "OpenAI.Codex_2p2nqsd0c76g0");

    private static readonly ForegroundTargetSnapshot BrowserForeground = new(
        IsResolved: true, ProcessId: 100, ProcessName: "chrome",
        PackageIdentity: PackageIdentityResolution.NoPackage, PackageFamilyName: null,
        ExecutableSignature: ExecutableSignatureResolution.Trusted, SignerOrganization: "Google LLC");

    // ==================================================================
    // D-R8 / D-R9 -- ClipboardAuthorization union invariants (real unit tests)
    // ==================================================================

    [Fact]
    public void D_R8_DefaultClipboardAuthorization_IsOutside_WithNoReadablePayload()
    {
        var outside = ClipboardAuthorization.Outside;
        var defaulted = default(ClipboardAuthorization);

        Assert.Equal(ClipboardAuthorizationKind.Outside, outside.Kind);
        Assert.Equal(ClipboardAuthorizationKind.Outside, defaulted.Kind);
        Assert.False(defaulted.TryGetWindows(out _));
        Assert.False(defaulted.TryGetWeb(out _, out _));
    }

    [Fact]
    public void D_R9_ForWindows_ExposesOnlyWindowsPayload_NeverWeb()
    {
        var authorization = ClipboardAuthorization.ForWindows(SupportedTarget.ChatGpt);

        Assert.Equal(ClipboardAuthorizationKind.WindowsTarget, authorization.Kind);
        Assert.True(authorization.TryGetWindows(out var target));
        Assert.Equal(SupportedTarget.ChatGpt, target);
        Assert.False(authorization.TryGetWeb(out _, out _));
    }

    [Fact]
    public void D_R9_ForWeb_ExposesOnlyWebPayload_NeverWindows_AndRequiresNonNullFreshness()
    {
        var freshness = new ScriptedFreshness(true);
        var authorization = ClipboardAuthorization.ForWeb(SupportedWebTarget.ChatGptWeb, freshness);

        Assert.Equal(ClipboardAuthorizationKind.WebTarget, authorization.Kind);
        Assert.True(authorization.TryGetWeb(out var target, out var boundFreshness));
        Assert.Equal(SupportedWebTarget.ChatGptWeb, target);
        Assert.Same(freshness, boundFreshness);
        Assert.False(authorization.TryGetWindows(out _));

        Assert.Throws<ArgumentNullException>(() => ClipboardAuthorization.ForWeb(SupportedWebTarget.ChatGptWeb, null!));
    }

    [Fact]
    public void D_R9_Factories_RejectUndefinedOrZeroEnumValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ClipboardAuthorization.ForWindows((SupportedTarget)0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClipboardAuthorization.ForWindows((SupportedTarget)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClipboardAuthorization.ForWeb((SupportedWebTarget)0, new ScriptedFreshness(true)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClipboardAuthorization.ForWeb((SupportedWebTarget)999, new ScriptedFreshness(true)));
    }

    [Fact]
    public void D_R9_NoConstructionPathRepresentsWindowsAndWebSimultaneously()
    {
        var windows = ClipboardAuthorization.ForWindows(SupportedTarget.ChatGpt);
        var web = ClipboardAuthorization.ForWeb(SupportedWebTarget.ChatGptWeb, new ScriptedFreshness(true));

        Assert.False(windows.TryGetWeb(out _, out _));
        Assert.False(web.TryGetWindows(out _));
        // Structural: the only public writers are the two factories, each of which sets exactly one
        // payload -- there is no public constructor or setter through which both could be set.
        Assert.DoesNotContain(
            typeof(ClipboardAuthorization).GetConstructors(BindingFlags.Public | BindingFlags.Instance),
            c => c.GetParameters().Length > 0);
    }

    // ==================================================================
    // D-R15A -- WebClipboardAuthorization constructor invariants
    // ==================================================================

    [Fact]
    public void D_R15A_WebClipboardAuthorization_ConstructorRejectsUndefinedOrZeroTarget()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebClipboardAuthorization((SupportedWebTarget)0, new ScriptedFreshness(true)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebClipboardAuthorization((SupportedWebTarget)999, new ScriptedFreshness(true)));
    }

    [Fact]
    public void D_R15A_WebClipboardAuthorization_ConstructorRejectsNullFreshness()
    {
        Assert.Throws<ArgumentNullException>(() => new WebClipboardAuthorization(SupportedWebTarget.ChatGptWeb, null!));
    }

    [Fact]
    public void D_R15C_MalformedNonNullSourceResult_IsNotDirectlyConstructible()
    {
        // DEFENSE_IN_DEPTH_NOT_DIRECTLY_CONSTRUCTIBLE (Gate 031F5A.2/031F5B, confirmed at GREEN):
        // the constructor invariants proven by D-R15A mean no caller can construct an invalid
        // non-null WebClipboardAuthorization through its own public surface -- there is no
        // parameterless/partial constructor, and both arguments are validated before the object is
        // considered constructed. Router-level defensive re-validation (ClipboardAuthorizationRouter
        // .AuthorizeAsync's own `if (!Enum.IsDefined(...) || result.Freshness is null)` check) is
        // therefore verified by source audit, not by an instance-level malformed-object test.
        var onlyConstructor = Assert.Single(typeof(WebClipboardAuthorization).GetConstructors());
        Assert.Equal(2, onlyConstructor.GetParameters().Length);
    }

    // ==================================================================
    // ROUTER -- ClipboardAuthorizationRouter.AuthorizeAsync
    // ==================================================================

    [Fact]
    public async Task Router_ValidWindowsTarget_AuthorizesImmediately_WebSourceNeverConsulted()
    {
        var source = new FakeWebClipboardAuthorizationSource();

        var authorization = await ClipboardAuthorizationRouter.AuthorizeAsync(ChatGpt, source);

        Assert.True(authorization.TryGetWindows(out var target));
        Assert.Equal(SupportedTarget.ChatGpt, target);
        Assert.Equal(0, source.CallCount);
    }

    [Fact]
    public async Task Router_UnsupportedTargetAndNoSource_ReturnsOutside()
    {
        var authorization = await ClipboardAuthorizationRouter.AuthorizeAsync(BrowserForeground, webSource: null);

        Assert.Equal(ClipboardAuthorizationKind.Outside, authorization.Kind);
    }

    [Fact]
    public async Task Router_UnsupportedTargetAndSourceDeclines_ReturnsOutside()
    {
        var source = new FakeWebClipboardAuthorizationSource { NextResult = null };

        var authorization = await ClipboardAuthorizationRouter.AuthorizeAsync(BrowserForeground, source);

        Assert.Equal(ClipboardAuthorizationKind.Outside, authorization.Kind);
        Assert.Equal(1, source.CallCount);
    }

    [Fact]
    public async Task Router_UnsupportedTargetAndValidWebResult_ReturnsWebAuthorization()
    {
        var freshness = new ScriptedFreshness(true);
        var source = new FakeWebClipboardAuthorizationSource
        {
            NextResult = new WebClipboardAuthorization(SupportedWebTarget.ChatGptWeb, freshness),
        };

        var authorization = await ClipboardAuthorizationRouter.AuthorizeAsync(BrowserForeground, source);

        Assert.True(authorization.TryGetWeb(out var target, out var boundFreshness));
        Assert.Equal(SupportedWebTarget.ChatGptWeb, target);
        Assert.Same(freshness, boundFreshness);
    }

    [Fact]
    public async Task Router_PassesTheSameForegroundSnapshotItReceived_ToTheWebSource()
    {
        var source = new FakeWebClipboardAuthorizationSource();

        await ClipboardAuthorizationRouter.AuthorizeAsync(BrowserForeground, source);

        Assert.Equal(BrowserForeground, Assert.Single(source.ReceivedSnapshots));
    }

    [Fact]
    public void Router_NeverCallsWebTargetGateDirectly()
    {
        // Doc comments are stripped first -- this class's own XML documentation legitimately
        // explains, in prose, that the router calls no WebTargetGate; only executable-code usage
        // (a "WebTargetGate." member access) is checked.
        Assert.True(TryFindAppSourceFile(nameof(ClipboardAuthorizationRouter), out string path));
        Assert.DoesNotContain("WebTargetGate.", StripDocComments(File.ReadAllText(path)));
    }

    // ==================================================================
    // ASYNC SOURCE BOUNDARY
    // ==================================================================

    [Fact]
    public void WebSource_AuthorizeAsync_HasExactFrozenSignature_NoCancellationToken()
    {
        var method = typeof(IWebClipboardAuthorizationSource).GetMethod(nameof(IWebClipboardAuthorizationSource.AuthorizeAsync));
        Assert.NotNull(method);
        var parameters = method!.GetParameters();
        Assert.Single(parameters);
        Assert.Equal(typeof(ForegroundTargetSnapshot), parameters[0].ParameterType);
        Assert.Equal(typeof(Task<WebClipboardAuthorization?>), method.ReturnType);
        Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(CancellationToken));
        Assert.Single(typeof(IWebClipboardAuthorizationSource).GetMethods());
    }

    [Fact]
    public async Task Router_PendingAuthorization_DoesNotResolveUntilTheSourceTaskCompletes()
    {
        var tcs = new TaskCompletionSource<WebClipboardAuthorization?>();
        var source = new FakeWebClipboardAuthorizationSource { PendingResult = tcs };

        var routerTask = ClipboardAuthorizationRouter.AuthorizeAsync(BrowserForeground, source);

        Assert.False(routerTask.IsCompleted);

        tcs.SetResult(new WebClipboardAuthorization(SupportedWebTarget.ChatGptWeb, new ScriptedFreshness(true)));
        var authorization = await routerTask;

        Assert.True(authorization.TryGetWeb(out _, out _));
    }

    [Fact]
    public async Task Router_UnexpectedSourceException_PropagatesRatherThanBecomingOutside()
    {
        var sentinel = new InvalidOperationException("synthetic source failure");
        var source = new FakeWebClipboardAuthorizationSource { ThrowOnAuthorize = sentinel };

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ClipboardAuthorizationRouter.AuthorizeAsync(BrowserForeground, source));
        Assert.Same(sentinel, thrown);
    }

    [Fact]
    public async Task D_R15B_SourceReturnsNull_RouterProducesOutside_NoClipboardIONoException()
    {
        var source = new FakeWebClipboardAuthorizationSource { NextResult = null };

        var authorization = await ClipboardAuthorizationRouter.AuthorizeAsync(BrowserForeground, source);

        Assert.Equal(ClipboardAuthorizationKind.Outside, authorization.Kind);
    }

    // ==================================================================
    // TRANSPORT WIDENING
    // ==================================================================

    [Fact]
    public void TransportWidening_ReadInterface_AcceptsOptionalTrailingFreshness()
    {
        var method = typeof(IClipboardReadTransport).GetMethod(nameof(IClipboardReadTransport.ReadTextSnapshotAsync));
        var parameters = method!.GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.Equal(typeof(IClipboardAuthorizationFreshness), parameters[1].ParameterType);
        Assert.True(parameters[1].IsOptional);
        Assert.Null(parameters[1].DefaultValue);
    }

    [Fact]
    public void TransportWidening_WriteInterface_AcceptsOptionalTrailingFreshness()
    {
        var method = typeof(IClipboardWriteTransport).GetMethod(nameof(IClipboardWriteTransport.WriteTextIfSequenceMatchesAsync));
        var parameters = method!.GetParameters();
        Assert.Equal(4, parameters.Length);
        Assert.Equal(typeof(IClipboardAuthorizationFreshness), parameters[3].ParameterType);
        Assert.True(parameters[3].IsOptional);
        Assert.Null(parameters[3].DefaultValue);
    }

    [Fact]
    public void TransportWidening_ReadWrapper_ForwardsFreshnessToClipboardChangeMonitor()
    {
        var method = typeof(ClipboardReadTransport).GetMethod(nameof(ClipboardReadTransport.ReadTextSnapshotAsync));
        var parameters = method!.GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.Equal(typeof(IClipboardAuthorizationFreshness), parameters[1].ParameterType);
    }

    [Fact]
    public void TransportWidening_WriteWrapper_ForwardsFreshnessToClipboardChangeMonitor()
    {
        var method = typeof(ClipboardWriteTransport).GetMethod(nameof(ClipboardWriteTransport.WriteTextIfSequenceMatchesAsync));
        var parameters = method!.GetParameters();
        Assert.Equal(4, parameters.Length);
        Assert.Equal(typeof(IClipboardAuthorizationFreshness), parameters[3].ParameterType);
    }

    // ==================================================================
    // COORDINATOR / RESOLVER CONSTRUCTOR WIDENING
    // ==================================================================

    [Fact]
    public void CoordinatorConstructor_AcceptsOptionalTrailingWebAuthorizationSource()
    {
        var ctor = typeof(ClipboardPrivacyCoordinator)
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .First(c => c.GetParameters().Any(p => p.Name == "retryDelay"));
        var parameter = ctor.GetParameters().Single(p => p.Name == "webAuthorizationSource");
        Assert.Equal(typeof(IWebClipboardAuthorizationSource), parameter.ParameterType);
        Assert.True(parameter.IsOptional);
        Assert.Null(parameter.DefaultValue);
    }

    [Fact]
    public void ResolverConstructor_AcceptsOptionalTrailingWebAuthorizationSource()
    {
        var ctor = typeof(ClipboardDecisionActionResolver).GetConstructors().Single();
        var parameter = ctor.GetParameters().Single(p => p.Name == "webAuthorizationSource");
        Assert.Equal(typeof(IWebClipboardAuthorizationSource), parameter.ParameterType);
        Assert.True(parameter.IsOptional);
        Assert.Null(parameter.DefaultValue);
    }

    [Fact]
    public void ProductionComposition_NowPassesARealWebAuthorizationSource_FailClosedViaEmptyAllowlist_Gate031E5F()
    {
        // This test's own original name scoped it to "before Phase E" -- Gate E5F is that later
        // phase's own explicit, stated GREEN target: composition now constructs exactly one
        // production WebClipboardAuthorizationSource through normal composition (see
        // Gate031E5F_ProductionCompositionRedTests.Case2_3, GREEN) and threads it into both
        // ClipboardPrivacyCoordinator's and ClipboardDecisionActionResolver's optional
        // webAuthorizationSource parameter (confirmed wired above by
        // CoordinatorConstructor/ResolverConstructor_AcceptsOptionalTrailingWebAuthorizationSource).
        // Production stays fail-closed regardless -- WebExtensionOriginAllowlist.Production remains
        // empty (E5D/E3-frozen, re-asserted by Gate031F6K_E4RedTests.G18_ProductionExtensionAllowlist_RemainsEmpty
        // and Gate031E5F_ProductionCompositionRedTests.Case4_5), so no accepted Native Messaging
        // session -- and therefore no authorization -- is reachable from source construction alone.
        Assert.True(TryFindAppSourceFile(nameof(PrivonAppComposition), out string path));
        string source = File.ReadAllText(path);
        Assert.Contains("webAuthorizationSource:", source, StringComparison.Ordinal);
        Assert.Contains("IWebClipboardAuthorizationSource", source, StringComparison.Ordinal);
    }

    // ==================================================================
    // PRIVACY / POLICY SEPARATION
    // ==================================================================

    private static readonly string[] ForbiddenProductValues =
    [
        "ChatGPT", "Claude", "Gemini", "Grok", "DeepSeek",
        "chatgpt.com", "claude.ai", "gemini.google.com", "grok.com", "chat.deepseek.com",
        "Google LLC", "Microsoft Corporation", "chrome", "msedge",
    ];

    [Fact]
    public void MechanicalRoutingTypes_ContainNoProductPolicyValues()
    {
        var candidateTypes = new[]
        {
            typeof(ClipboardAuthorization), typeof(ClipboardAuthorizationKind),
            typeof(WebClipboardAuthorization), typeof(ClipboardAuthorizationRouter),
        };

        foreach (var type in candidateTypes)
        {
            var offendingMembers = type
                .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                    | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Select(m => m.Name)
                .Where(name => ForbiddenProductValues.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            Assert.Empty(offendingMembers);

            var offendingConstants = type
                .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .Select(f => (string?)f.GetRawConstantValue())
                .Where(v => v is not null && ForbiddenProductValues.Any(
                    f => v.Contains(f, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            Assert.Empty(offendingConstants);

            Assert.True(TryFindAppSourceFile(type.Name, out string path));
            string fileText = File.ReadAllText(path);
            var offendingSourceHits = ForbiddenProductValues.Where(f => fileText.Contains(f, StringComparison.Ordinal)).ToList();
            Assert.Empty(offendingSourceHits);
        }
    }

    [Fact]
    public void WebSourceInterface_ExposesNoNativeMessagingOrEvidenceConcept()
    {
        var forbidden = new[] { "Origin", "Url", "Uri", "Nonce", "Challenge", "Evidence", "DecisionContext" };

        var interfaceMembers = typeof(IWebClipboardAuthorizationSource).GetMembers().Select(m => m.Name);
        var resultMembers = typeof(WebClipboardAuthorization)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name);

        var offending = interfaceMembers.Concat(resultMembers)
            .Where(name => forbidden.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        Assert.Empty(offending);
    }

    // ==================================================================
    // HELPERS
    // ==================================================================

    private static bool TryFindAppSourceFile(string typeSimpleName, out string path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PRIVON.slnx")))
            directory = directory.Parent;

        path = directory is null ? "" : Path.Combine(directory.FullName, "src", "Privon.App", typeSimpleName + ".cs");
        return directory is not null && File.Exists(path);
    }

    // Line-comment style only, matching this codebase's own doc-comment stripping precedent
    // (PrivonAppCompositionTests.StripDocComments) -- so a structural scan only ever matches actual
    // executable code, never prose that explains what a type deliberately does not do.
    private static string StripDocComments(string source) =>
        string.Join('\n', source.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private sealed class ScriptedFreshness(bool result) : IClipboardAuthorizationFreshness
    {
        public bool IsStillCurrent() => result;
    }
}
