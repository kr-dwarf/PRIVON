namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate E5D.C -- the production Native Messaging host registrar (host name
/// "com.privon.host", never the E5B dev-only "com.privon.devmeasure"). Implements the FROZEN
/// ownership contract established by Gate E5C/E5D.B's own behavioral RED suite
/// (Gate031E5C_NativeMessagingHostRegistrarRedTests.cs): the exact host-subkey DEFAULT VALUE is the
/// SOLE manifest-path ownership witness -- never manifest contents, never filename/directory
/// similarity, never case-insensitive comparison.
///
/// OWNED: leaf exists, default value exactly (Ordinal) equals the expected manifest path, manifest
/// exists.
/// STALE: same exact witness as OWNED, but the expected manifest is ABSENT.
/// FOREIGN: leaf exists, default value missing/empty/different (including a case-only difference)
/// -- NO mutation of any kind.
/// UNWITNESSED ORPHAN: leaf absent, regardless of what exists at the expected manifest path -- path
/// alone is never ownership authority, so NO mutation of any kind.
///
/// FROZEN MUTATION ORDER (Gate E5D.B section 6/8, recoverability): fresh Install sets the registry
/// witness BEFORE writing the manifest, and OWNED Uninstall deletes the manifest BEFORE the registry
/// leaf -- so a mid-operation failure always leaves a provable, recoverable STALE state, never an
/// unwitnessed orphan. Never reversed, never rolled back.
///
/// This type owns 100% of the ownership POLICY; <see cref="INativeMessagingHostRegistrationEnvironment"/>
/// supplies only mechanical primitives and carries no judgment of its own. NOT wired into any
/// composition root, startup path, or browser session in this gate (Gate E5D scope: registrar LOGIC
/// only) -- <see cref="PrivonAppComposition"/> does not reference this type.
/// </summary>
internal sealed class NativeMessagingHostRegistrar
{
    private readonly INativeMessagingHostRegistrationEnvironment _environment;

    public NativeMessagingHostRegistrar(INativeMessagingHostRegistrationEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _environment = environment;
    }

    /// <summary>FRESH install only: exact leaf absent AND expected manifest absent. Any other
    /// starting state (leaf already exists -- witnessed or FOREIGN -- or an unwitnessed orphan
    /// manifest already sitting at the expected path) is a conservative no-op; this gate's frozen
    /// contract requires no repair/update semantics.</summary>
    public void Install(NativeMessagingBrowser browser, NativeMessagingHostRegistrationSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        string expectedPath = NativeMessagingHostRegistrationLayout.ExpectedManifestPath(browser);

        if (_environment.SubkeyExists(browser))
        {
            // Leaf already exists (witnessed or FOREIGN) -- not FRESH. Fail closed.
            return;
        }

        if (_environment.ManifestExists(expectedPath))
        {
            // UNWITNESSED ORPHAN: a file already sits at the expected path with no registry
            // witness proving PRIVON put it there. Never overwrite/adopt it.
            return;
        }

        string manifestJson = NativeMessagingHostRegistrationLayout.BuildManifestJson(spec);

        // FROZEN ORDER: registry witness FIRST, manifest SECOND. If WriteManifest below throws, the
        // witness already committed leaves an ownership-provable STALE state -- never rolled back,
        // never an unwitnessed orphan.
        _environment.SetSubkeyDefaultValue(browser, expectedPath);
        _environment.WriteManifest(expectedPath, manifestJson);
    }

    /// <summary>Reads the exact leaf/default witness first. Absent leaf or a non-matching (including
    /// case-only-different) default value never mutates anything. OWNED/STALE removes exactly the
    /// witnessed artifacts, manifest before leaf.</summary>
    public void Uninstall(NativeMessagingBrowser browser)
    {
        if (!_environment.SubkeyExists(browser))
        {
            // No witness at all -- UNWITNESSED ORPHAN (or genuinely nothing registered). Never
            // mutate, regardless of what might exist at the expected path.
            return;
        }

        string expectedPath = NativeMessagingHostRegistrationLayout.ExpectedManifestPath(browser);
        string? currentDefault = _environment.GetSubkeyDefaultValue(browser);
        bool isWitnessed = !string.IsNullOrEmpty(currentDefault)
            && string.Equals(currentDefault, expectedPath, StringComparison.Ordinal);

        if (!isWitnessed)
        {
            // FOREIGN (missing/empty/different default value, including a case-only difference
            // under Ordinal comparison) -- NO mutation.
            return;
        }

        // OWNED or STALE (both share this exact witness -- only the manifest's actual presence
        // differs). FROZEN ORDER: manifest FIRST, leaf SECOND. If DeleteSubkey below fails after the
        // manifest is already gone, the residual state is STALE and remains ownership-provable --
        // never recreate the manifest to compensate.
        if (_environment.ManifestExists(expectedPath))
        {
            _environment.DeleteManifest(expectedPath);
        }
        _environment.DeleteSubkey(browser);
    }

}
