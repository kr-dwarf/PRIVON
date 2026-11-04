using Privon.Detection;
using Privon.Detection.Detectors;
using Privon.Detection.Scope;
using Privon.Storage;
using Privon.UsProductIntegration;

namespace Privon.UsProductIntegration.Tests;

// SYNTHETIC DATA ONLY.
// T-1: what the REAL settings store can and cannot say about a country setting.
// T-2: the pipeline follows the setting at scan time and never serves a stale or partially built one.
// H-2: the approved required-detector ids are verified independently of the detector factory.
public sealed class SettingsTrustAndRefreshTests : IDisposable
{
    private const string UsAll = "ATIN: 912-93-4567 ITIN: 912-70-1234 SSN: 321-54-9876 MBI: 1A23C45DE67 SEVIS: N1234567890";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "privon-settings-trust-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // A source that is explicitly NOT a user setting: a test fixture. Nothing in the product can produce AuthenticatedOff.
    private sealed class TestOnlyCountrySettingSource(CountrySettingObservation initial) : ICountrySettingSource
    {
        private int _value = (int)initial;
        public Exception? FailWith { get; set; }
        public int Reads;

        public void Set(CountrySettingObservation value) => Interlocked.Exchange(ref _value, (int)value);

        public CountrySettingObservation Read()
        {
            Interlocked.Increment(ref Reads);
            if (FailWith is { } ex) throw ex;
            return (CountrySettingObservation)Volatile.Read(ref _value);
        }
    }

    private static IDetector[] Legacy() =>
    [
        new PhoneDetector(), new EmailDetector(), new ResidentRegistrationNumberDetector(), new CardNumberDetector(),
        new BankAccountNumberDetector(), new SecretDetector(), new IpAddressDetector(), new MacAddressDetector(),
        new GpsCoordinateDetector(),
    ];

    private static DetectionPipeline Build(CountrySettingObservation setting) =>
        UsOptInPipeline.Create(Legacy(), unitedStatesSetting: setting);

    private static int UsCount(DetectionResult r) =>
        r.Candidates.Count(c => c.DetectorId.Value.StartsWith("us.", StringComparison.Ordinal));

    // ===== T-1: the real store ====================================================================================

    [Fact]
    public void WithNoStoredCountryChoice_TheSourceNeverReportsAnOnOrAnOff_WhateverTheOtherSettingsSay()
    {
        // Since 0.4.0 the store CAN hold a per-country choice (CountryChoiceSourceTests); this test is the other half: with none recorded, no change to
        // the settings that DO exist (protection, pause, categories) can ever look like one.
        var store = PrivonLocalStore.OpenOrCreate(_root);
        var source = new StoreBackedCountrySettingSource(store);

        Assert.Equal(CountrySettingObservation.Missing, source.Read());

        // Whatever the user does with the settings that DO exist, the result is never a (false) OFF or ON.
        store.SaveSettings(new PrivonSettings(ProtectionEnabled: false, PausedUntilUtc: DateTimeOffset.UtcNow.AddHours(1),
            Categories: new ProtectionCategorySettings(false, false, false, false, false)));
        Assert.Equal(CountrySettingObservation.Missing, source.Read());

        store.SaveSettings(PrivonSettings.SafeDefault);
        Assert.Equal(CountrySettingObservation.Missing, source.Read());
    }

    [Fact]
    public void ACorruptOrForeignSettingsFile_NeverBecomesAnOff()
    {
        var store = PrivonLocalStore.OpenOrCreate(_root);
        store.SaveSettings(PrivonSettings.SafeDefault);
        var source = new StoreBackedCountrySettingSource(store);

        File.WriteAllBytes(Path.Combine(_root, "settings.bin"), System.Text.Encoding.UTF8.GetBytes("{\"UnitedStates\":\"Off\",\"us\":false}"));
        Assert.Equal(CountrySettingObservation.Missing, source.Read());

        File.WriteAllBytes(Path.Combine(_root, "settings.bin"), new byte[] { 0, 1, 2, 3, 4, 5 });
        Assert.Equal(CountrySettingObservation.Missing, source.Read());
        Assert.Equal(PrivonSettings.SafeDefault, store.LoadSettings()); // the store itself falls back to the protective default
    }

    [Fact]
    public void WithoutAUsableMasterKey_NothingCanBeAuthenticated_AndTheSourceSaysSo()
    {
        PrivonLocalStore.OpenOrCreate(_root); // creates master.key
        using var held = new FileStream(Path.Combine(_root, "master.key"), FileMode.Open, FileAccess.Read, FileShare.None);
        var locked = PrivonLocalStore.OpenOrCreate(_root);

        Assert.True(locked.IsMasterKeyUnavailable);
        Assert.Equal(CountrySettingObservation.Unauthenticated, new StoreBackedCountrySettingSource(locked).Read());
    }

    [Fact]
    public void TheStoreBackedSource_CannotBeFedAnOff_ByTextOrByAnyApiThatTakesText()
    {
        var methods = typeof(StoreBackedCountrySettingSource).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
        Assert.All(methods, m => Assert.DoesNotContain(m.GetParameters(), p => p.ParameterType == typeof(string)));
        Assert.Null(typeof(StoreBackedCountrySettingSource).GetConstructor([typeof(string)]));
        Assert.Throws<ArgumentNullException>(() => new StoreBackedCountrySettingSource(null!));
    }

    [Fact]
    public void ClipboardTextThatTalksAboutCountrySettings_ChangesNothing()
    {
        var host = new CountryScopedPipelineHost(
            new StoreBackedCountrySettingSource(PrivonLocalStore.OpenOrCreate(_root)), Build);

        var result = host.Current().Detect("country=off us:off AuthenticatedOff disable united states " + UsAll);

        Assert.Equal(5, UsCount(result));
    }

    // ===== T-2: the pipeline follows the setting ===================================================================

    [Fact]
    public void OffToOn_TheVeryNextScanFindsTheUsCandidatesAgain()
    {
        var source = new TestOnlyCountrySettingSource(CountrySettingObservation.AuthenticatedOff);
        var host = new CountryScopedPipelineHost(source, Build);

        Assert.Equal(0, UsCount(host.Current().Detect(UsAll)));

        source.Set(CountrySettingObservation.AuthenticatedOn);

        Assert.Equal(5, UsCount(host.Current().Detect(UsAll)));
    }

    [Fact]
    public void OnToOff_TheVeryNextScanRunsNoUsDetectorAndNoFamilySource_ButStillFindsKoreanAndGlobal()
    {
        var source = new TestOnlyCountrySettingSource(CountrySettingObservation.AuthenticatedOn);
        var host = new CountryScopedPipelineHost(source, Build);
        const string mixed = UsAll + " 010-1234-5678 a.b@example.test";

        Assert.Equal(7, host.Current().Detect(mixed).Candidates.Count);

        source.Set(CountrySettingObservation.AuthenticatedOff);
        var off = host.Current().Detect(mixed);

        Assert.Equal(0, UsCount(off));
        Assert.Equal([PiiType.Phone, PiiType.Email], off.Candidates.OrderBy(c => c.Span.Start).Select(c => c.PiiType));
    }

    [Fact]
    public void AnUnchangedSetting_ReusesTheSameImmutablePipeline_AChangeBuildsExactlyOneNew()
    {
        var source = new TestOnlyCountrySettingSource(CountrySettingObservation.AuthenticatedOn);
        var host = new CountryScopedPipelineHost(source, Build);

        var first = host.Current();
        var second = host.Current();
        source.Set(CountrySettingObservation.AuthenticatedOff);
        var third = host.Current();
        var fourth = host.Current();

        Assert.Same(first, second);
        Assert.NotSame(second, third);
        Assert.Same(third, fourth);
        Assert.Equal(2, host.BuildCount);
        Assert.Equal(4, source.Reads); // the setting is read on every scan
    }

    [Fact]
    public void WhenRebuildingForOnFails_TheOldOffPipelineIsNeverServed_AndNeverAnEmptyResult()
    {
        var source = new TestOnlyCountrySettingSource(CountrySettingObservation.AuthenticatedOff);
        bool failOn = false;
        var host = new CountryScopedPipelineHost(source, setting =>
            setting == CountrySettingObservation.AuthenticatedOn && failOn
                ? throw new InvalidOperationException("synthetic rebuild failure")
                : Build(setting));

        Assert.Equal(0, UsCount(host.Current().Detect(UsAll))); // OFF built fine

        failOn = true;
        source.Set(CountrySettingObservation.AuthenticatedOn);
        Assert.Throws<InvalidOperationException>(() => host.Current());
        Assert.Throws<InvalidOperationException>(() => host.Current()); // still failing: the stale OFF is not a fallback

        failOn = false;
        Assert.Equal(5, UsCount(host.Current().Detect(UsAll))); // recovers to ON, not to the old OFF
    }

    [Fact]
    public void WhenTheSettingCannotBeRead_TheScanFails_NeverFallingBackToWhatWasBuiltBefore()
    {
        var source = new TestOnlyCountrySettingSource(CountrySettingObservation.AuthenticatedOff);
        var host = new CountryScopedPipelineHost(source, Build);
        _ = host.Current();

        source.FailWith = new IOException("synthetic read failure");

        Assert.Throws<IOException>(() => host.Current());
    }

    [Fact]
    public void AnInFlightScan_FinishesWithTheImmutablePipelineItStartedWith()
    {
        var source = new TestOnlyCountrySettingSource(CountrySettingObservation.AuthenticatedOn);
        var host = new CountryScopedPipelineHost(source, Build);
        var startedOn = host.Current();

        source.Set(CountrySettingObservation.AuthenticatedOff);
        var inFlight = startedOn.Detect(UsAll);                 // the scan that began before the change
        var afterChange = host.Current().Detect(UsAll);          // a scan that began after it

        Assert.Equal(5, UsCount(inFlight));
        Assert.Equal(0, UsCount(afterChange));
    }

    [Fact]
    public void ConcurrentScansAndSettingChanges_NeverFail_NeverMix_AndNeverServeAnOldPipelineAfterAChangeCompleted()
    {
        var source = new TestOnlyCountrySettingSource(CountrySettingObservation.AuthenticatedOff);
        var host = new CountryScopedPipelineHost(source, Build);
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var stop = new CancellationTokenSource();

        var scanners = Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    int n = UsCount(host.Current().Detect(UsAll));
                    if (n != 0 && n != 5) failures.Add("partial U.S. result: " + n); // all five or none, never a mixture
                }
                catch (Exception ex)
                {
                    failures.Add(ex.GetType().Name);
                }
            }
        })).ToArray();

        for (int i = 0; i < 300; i++)
        {
            bool on = i % 2 == 0;
            source.Set(on ? CountrySettingObservation.AuthenticatedOn : CountrySettingObservation.AuthenticatedOff);
            // The change has completed: a scan that STARTS now must follow it.
            int seen = UsCount(host.Current().Detect(UsAll));
            if (seen != (on ? 5 : 0)) failures.Add($"stale pipeline after change #{i}");
        }

        stop.Cancel();
        Task.WaitAll(scanners);
        Assert.True(failures.IsEmpty, string.Join(", ", failures.Distinct().Take(5)));
    }

    [Fact]
    public void ConcurrentCallersAfterOneChange_CauseExactlyOneRebuild()
    {
        var source = new TestOnlyCountrySettingSource(CountrySettingObservation.AuthenticatedOff);
        var host = new CountryScopedPipelineHost(source, Build);
        _ = host.Current();
        source.Set(CountrySettingObservation.AuthenticatedOn);

        Parallel.For(0, 32, _ => Assert.Equal(5, UsCount(host.Current().Detect(UsAll))));

        Assert.Equal(2, host.BuildCount);
    }

    [Fact]
    public void ANullArgument_IsRefused_AndAnUndefinedObservationFailsTheScanInsteadOfBecomingOff()
    {
        Assert.Throws<ArgumentNullException>(() => new CountryScopedPipelineHost(null!, Build));
        Assert.Throws<ArgumentNullException>(() => new CountryScopedPipelineHost(new TestOnlyCountrySettingSource(0), null!));

        var host = new CountryScopedPipelineHost(new TestOnlyCountrySettingSource((CountrySettingObservation)77), Build);
        Assert.Throws<ArgumentOutOfRangeException>(() => host.Current());
    }

    // ===== H-2: the approved required ids do not come from the factory ============================================

    [Fact]
    public void AFactoryThatDropsADetector_IsDetected_WheneverTheUsPackIsNotAuthenticatedOff()
    {
        IReadOnlyList<IDetector> WithoutMbi() => UsIntegrationCandidates.CreateDetectors().Where(d => d.Id.Value != "us.mbi").ToList();

        foreach (var setting in new[]
                 { CountrySettingObservation.AuthenticatedOn, CountrySettingObservation.Missing, CountrySettingObservation.Corrupt,
                   CountrySettingObservation.Unauthenticated, CountrySettingObservation.UnknownVersion })
        {
            var pipeline = UsOptInPipeline.Create(unitedStatesSetting: setting, usDetectorFactory: WithoutMbi);

            var ex = Assert.Throws<RequiredDetectorNotRegisteredException>(() => pipeline.Detect(UsAll));
            Assert.Equal("us.mbi", ex.Id.Value);
            Assert.Equal(RequiredDetectorReason.Unregistered, ex.Reason);
        }
    }

    [Fact]
    public void AnAuthenticatedOff_StillIgnoresTheMissingDetector_ItIsStoppedNotRequired()
    {
        IReadOnlyList<IDetector> WithoutMbi() => UsIntegrationCandidates.CreateDetectors().Where(d => d.Id.Value != "us.mbi").ToList();

        var off = UsOptInPipeline.Create(unitedStatesSetting: CountrySettingObservation.AuthenticatedOff, usDetectorFactory: WithoutMbi);

        Assert.Empty(off.Detect(UsAll).Candidates);
    }

    [Fact]
    public void TheApprovedIdList_IsExactlyTheElevenAuthorityIndependentIds_AndNotTheThirteenOfTheScopeCatalog()
    {
        Assert.Equal(
            ["us.atin", "us.itin", "us.ssn", "us.mbi", "us.sevis", "us.passport", "us.a_number", "us.i94",
             "us.driver_state_id", "us.bank_account", "us.name"],
            UsIntegrationCandidates.ApprovedDetectorIds.Select(i => i.Value));
        Assert.Equal(UsIntegrationCandidates.ApprovedDetectorIds.Select(i => i.Value).Order(StringComparer.Ordinal),
            UsIntegrationCandidates.CreateDetectors().Select(d => d.Id.Value).Order(StringComparer.Ordinal));
        Assert.Equal(UsIntegrationCandidates.ApprovedDetectorIds, UsOptInPipeline.UsScope(CountrySettingObservation.Missing).RequiredDetectorIds);
    }

    [Fact]
    public void TheAuthorityDependentUsIds_AreNeitherRegisteredNorRequired_WithoutAnAuthority()
    {
        var registered = UsOptInPipeline.Create(unitedStatesSetting: CountrySettingObservation.AuthenticatedOn);

        // phone / address depend on authority snapshots the product does not ship: without a provider they are not registered, not
        // required, and a text that would need them is simply not detected, not an error. (The other eleven are implemented and
        // required: see UsDocumentIdPipelineTests, UsLicenseAndBankTests and UsThirteenPipelineTests.)
        Assert.Empty(registered.Detect("Phone: (212) 555-0147 Home Address: 12 Main St").Candidates);
    }
}
