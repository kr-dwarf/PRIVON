namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate E5G.P3 -- the store-independent, App-level Native Messaging registration
/// coordinator. Owns exactly one <see cref="NativeMessagingHostRegistrar"/> (E5D, frozen) against
/// exactly one <see cref="INativeMessagingHostRegistrationEnvironment"/>, and resolves the trusted
/// host executable path through an injected provider whose production value is
/// <see cref="Environment.ProcessPath"/> -- never <see cref="System.Reflection.Assembly.Location"/>,
/// never <see cref="AppContext.BaseDirectory"/> (see <see cref="WebPipeEndpoint"/>'s own doc for why
/// those diverge under this project's single-file publish).
///
/// COMMANDER_CONTRACT (Gate E5G.P2, frozen): registration is EXPLICIT provisioning/repair only --
/// never every-startup behavior. Construction performs ZERO mutation. With no verified extension
/// origin supplied by a caller, zero <see cref="NativeMessagingHostRegistrar.Install"/> calls are
/// even reachable. Chrome and Edge are mechanically independent: every method takes exactly one
/// <see cref="NativeMessagingBrowser"/>, and there is no cross-browser method and no automatic
/// rollback of one browser's successful registration because the other failed.
///
/// OWNERSHIP_VS_READINESS (the same registry-witness-only rule E5D already froze, re-derived here
/// read-only): <see cref="Inspect"/> classifies state purely from
/// <see cref="INativeMessagingHostRegistrationEnvironment.SubkeyExists"/>/
/// <see cref="INativeMessagingHostRegistrationEnvironment.GetSubkeyDefaultValue"/> compared
/// <see cref="StringComparison.Ordinal"/> against <see cref="NativeMessagingHostRegistrationLayout.ExpectedManifestPath"/>
/// -- manifest content is read only AFTER that witness already proves ownership, purely to
/// distinguish <see cref="NativeMessagingRegistrationReadiness.Ready"/> from
/// <see cref="NativeMessagingRegistrationReadiness.OwnedNeedsRepair"/>.
///
/// NO_RESULT_FROM_REGISTRAR: <see cref="NativeMessagingHostRegistrar.Install"/>/
/// <see cref="NativeMessagingHostRegistrar.Uninstall"/> stay exactly <see langword="void"/> (E5D,
/// frozen) -- this type never changes that contract. Every outcome below is determined by re-running
/// <see cref="Inspect"/> AFTER a mutation attempt, never by assuming a non-throwing call succeeded.
/// </summary>
internal sealed class NativeMessagingHostRegistrationCoordinator
{
    private readonly INativeMessagingHostRegistrationEnvironment _environment;
    private readonly NativeMessagingHostRegistrar _registrar;
    private readonly Func<string?> _hostExecutablePathProvider;

    public NativeMessagingHostRegistrationCoordinator(INativeMessagingHostRegistrationEnvironment environment)
        : this(environment, () => Environment.ProcessPath)
    {
    }

    /// <summary>Test-only: overrides how this type resolves the trusted host executable path.
    /// Production always uses <see cref="Environment.ProcessPath"/> (see this type's own class doc)
    /// -- never a hardcoded/build-configuration-specific path.</summary>
    internal NativeMessagingHostRegistrationCoordinator(
        INativeMessagingHostRegistrationEnvironment environment, Func<string?> hostExecutablePathProvider)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(hostExecutablePathProvider);
        _environment = environment;
        _registrar = new NativeMessagingHostRegistrar(environment);
        _hostExecutablePathProvider = hostExecutablePathProvider;
    }

    /// <summary>Read-only. Never mutates registry or filesystem state under any input or reachable
    /// state.</summary>
    public NativeMessagingRegistrationReadiness Inspect(NativeMessagingBrowser browser, string? extensionOrigin)
    {
        if (string.IsNullOrWhiteSpace(extensionOrigin))
            return NativeMessagingRegistrationReadiness.Failed;
        if (!TryResolveHostExecutablePath(out string hostExecutablePath))
            return NativeMessagingRegistrationReadiness.Failed;

        return InspectCore(browser, hostExecutablePath, extensionOrigin);
    }

    /// <summary>Mutates ONLY when the pre-inspection state is
    /// <see cref="NativeMessagingRegistrationReadiness.Fresh"/> -- every other state (already
    /// <see cref="NativeMessagingRegistrationReadiness.Ready"/>,
    /// <see cref="NativeMessagingRegistrationReadiness.OwnedNeedsRepair"/> (repair-required, never
    /// auto-repaired here), <see cref="NativeMessagingRegistrationReadiness.ForeignBlocked"/>,
    /// <see cref="NativeMessagingRegistrationReadiness.OrphanBlocked"/>, or unusable input) is
    /// returned unchanged with zero mutation. Success requires the POST-mutation state to actually be
    /// <see cref="NativeMessagingRegistrationReadiness.Ready"/> -- a non-throwing
    /// <see cref="NativeMessagingHostRegistrar.Install"/> call is never itself treated as success.</summary>
    public NativeMessagingRegistrationReadiness Provision(NativeMessagingBrowser browser, string? extensionOrigin)
    {
        if (string.IsNullOrWhiteSpace(extensionOrigin))
            return NativeMessagingRegistrationReadiness.Failed;
        if (!TryResolveHostExecutablePath(out string hostExecutablePath))
            return NativeMessagingRegistrationReadiness.Failed;

        var readiness = InspectCore(browser, hostExecutablePath, extensionOrigin);
        if (readiness != NativeMessagingRegistrationReadiness.Fresh)
            return readiness;

        try
        {
            _registrar.Install(browser, new NativeMessagingHostRegistrationSpec(hostExecutablePath, extensionOrigin));
        }
        catch
        {
            return NativeMessagingRegistrationReadiness.Failed;
        }

        return InspectCore(browser, hostExecutablePath, extensionOrigin);
    }

    /// <summary>Mutates ONLY when the pre-inspection state is
    /// <see cref="NativeMessagingRegistrationReadiness.OwnedNeedsRepair"/> -- every other state
    /// (already <see cref="NativeMessagingRegistrationReadiness.Ready"/> (no destructive churn),
    /// <see cref="NativeMessagingRegistrationReadiness.ForeignBlocked"/>,
    /// <see cref="NativeMessagingRegistrationReadiness.OrphanBlocked"/>,
    /// <see cref="NativeMessagingRegistrationReadiness.Fresh"/>, or unusable input) is returned
    /// unchanged with zero mutation. Operation order matches E5D's own recoverability precedent:
    /// <see cref="NativeMessagingHostRegistrar.Uninstall"/> first, THEN
    /// <see cref="NativeMessagingHostRegistrar.Install"/>. Success requires the FINAL post-mutation
    /// state to actually be <see cref="NativeMessagingRegistrationReadiness.Ready"/>.</summary>
    public NativeMessagingRegistrationReadiness Repair(NativeMessagingBrowser browser, string? extensionOrigin)
    {
        if (string.IsNullOrWhiteSpace(extensionOrigin))
            return NativeMessagingRegistrationReadiness.Failed;
        if (!TryResolveHostExecutablePath(out string hostExecutablePath))
            return NativeMessagingRegistrationReadiness.Failed;

        var readiness = InspectCore(browser, hostExecutablePath, extensionOrigin);
        if (readiness != NativeMessagingRegistrationReadiness.OwnedNeedsRepair)
            return readiness;

        try
        {
            _registrar.Uninstall(browser);
        }
        catch
        {
            return NativeMessagingRegistrationReadiness.Failed;
        }

        try
        {
            _registrar.Install(browser, new NativeMessagingHostRegistrationSpec(hostExecutablePath, extensionOrigin));
        }
        catch
        {
            return NativeMessagingRegistrationReadiness.Failed;
        }

        return InspectCore(browser, hostExecutablePath, extensionOrigin);
    }

    private bool TryResolveHostExecutablePath(out string hostExecutablePath)
    {
        string? raw = _hostExecutablePathProvider();
        if (string.IsNullOrWhiteSpace(raw))
        {
            hostExecutablePath = "";
            return false;
        }

        hostExecutablePath = raw;
        return true;
    }

    /// <summary>The single read-only classification algorithm -- re-derives the SAME OWNED/STALE/
    /// FOREIGN/orphan witness judgment <see cref="NativeMessagingHostRegistrar"/> already froze (Gate
    /// E5D), against the SAME shared <see cref="NativeMessagingHostRegistrationLayout"/> path/manifest
    /// authority the registrar itself now uses, so the two can never independently drift.</summary>
    private NativeMessagingRegistrationReadiness InspectCore(
        NativeMessagingBrowser browser, string hostExecutablePath, string extensionOrigin)
    {
        try
        {
            string expectedPath = NativeMessagingHostRegistrationLayout.ExpectedManifestPath(browser);

            if (!_environment.SubkeyExists(browser))
            {
                return _environment.ManifestExists(expectedPath)
                    ? NativeMessagingRegistrationReadiness.OrphanBlocked
                    : NativeMessagingRegistrationReadiness.Fresh;
            }

            string? currentDefault = _environment.GetSubkeyDefaultValue(browser);
            bool isWitnessed = !string.IsNullOrEmpty(currentDefault)
                && string.Equals(currentDefault, expectedPath, StringComparison.Ordinal);

            if (!isWitnessed)
                return NativeMessagingRegistrationReadiness.ForeignBlocked;

            if (!_environment.ManifestExists(expectedPath))
                return NativeMessagingRegistrationReadiness.OwnedNeedsRepair;

            string? manifestContent = _environment.ReadManifest(expectedPath);
            string expectedManifestJson = NativeMessagingHostRegistrationLayout.BuildManifestJson(
                new NativeMessagingHostRegistrationSpec(hostExecutablePath, extensionOrigin));

            return manifestContent is not null
                && string.Equals(manifestContent, expectedManifestJson, StringComparison.Ordinal)
                    ? NativeMessagingRegistrationReadiness.Ready
                    : NativeMessagingRegistrationReadiness.OwnedNeedsRepair;
        }
        catch
        {
            return NativeMessagingRegistrationReadiness.Failed;
        }
    }
}
