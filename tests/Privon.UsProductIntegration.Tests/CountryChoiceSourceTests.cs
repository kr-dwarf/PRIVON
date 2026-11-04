using System.Text;
using Privon.Detection;
using Privon.Detection.Scope;
using Privon.Storage;
using Privon.UsProductIntegration;

namespace Privon.UsProductIntegration.Tests;

// SYNTHETIC DATA ONLY. The store-backed country source over the REAL PrivonLocalStore, up to the detection scope: an intact Off record stops the U.S.
// pack and nothing else does. Every test that records a choice calls SaveCountryChoice directly, which stands in for a Settings user action the
// product does NOT have yet -- so these tests show that a stored choice is read and honoured correctly, not that any user made one.
public sealed class CountryChoiceSourceTests : IDisposable
{
    private const string Us = "UnitedStates";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CountryChoiceSource", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private PrivonLocalStore Open() => PrivonLocalStore.OpenOrCreate(_root);

    private string CountryFile => Path.Combine(_root, "country-settings.bin");

    private static IEnumerable<string> UsFound(DetectionPipeline pipeline, string text) =>
        pipeline.Detect(text).Candidates.Select(c => c.DetectorId.Value).Where(id => id.StartsWith("us.", StringComparison.Ordinal)).Distinct();

    private const string UsText = "SSN: 321-54-9876 Phone: 212-555-1212 Address: 123 Main ST Full Name: Alice O'Neil";

    // ===== the mapping ==========================================================================================================

    [Fact]
    public void EachStoredState_MapsToItsOwnObservation_AndNothingElseIsAnOff()
    {
        var store = Open();
        var source = new StoreBackedCountrySettingSource(store);
        Assert.Equal(CountrySettingObservation.Missing, source.Read());

        store.SaveCountryChoice(Us, enabled: false, CountryChoiceOrigin.SettingsUserAction);
        Assert.Equal(CountrySettingObservation.AuthenticatedOff, source.Read());

        store.SaveCountryChoice(Us, enabled: true, CountryChoiceOrigin.SettingsUserAction);
        Assert.Equal(CountrySettingObservation.AuthenticatedOn, source.Read());

        byte[] good = File.ReadAllBytes(CountryFile);
        var tampered = (byte[])good.Clone();
        tampered[^1] ^= 1;
        File.WriteAllBytes(CountryFile, tampered);
        Assert.Equal(CountrySettingObservation.Unauthenticated, source.Read());

        File.WriteAllBytes(CountryFile, [1, 2, 3]);
        Assert.Equal(CountrySettingObservation.Corrupt, source.Read());

        Assert.True(MasterKeyStore.TryLoad(Path.Combine(_root, "master.key"), out var key));
        var newer = SecureEnvelopeCodec.Encrypt(key, StorePurpose.CountrySettings,
            Encoding.UTF8.GetBytes("{\"FormatVersion\":3,\"Revision\":99,\"Choices\":[]}"));
        File.WriteAllBytes(CountryFile, newer.Encode());
        Assert.Equal(CountrySettingObservation.UnknownVersion, source.Read());
    }

    [Fact]
    public void WithoutAUsableKey_TheSourceSaysUnauthenticated_NeverOff()
    {
        Open().SaveCountryChoice(Us, false, CountryChoiceOrigin.SettingsUserAction);
        using var held = new FileStream(Path.Combine(_root, "master.key"), FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.Equal(CountrySettingObservation.Unauthenticated, new StoreBackedCountrySettingSource(Open()).Read());
    }

    [Fact]
    public void AnOffForAnotherCountry_NeverAffectsTheUnitedStates()
    {
        var store = Open();
        store.SaveCountryChoice("Korea", enabled: false, CountryChoiceOrigin.SettingsUserAction);

        Assert.Equal(CountrySettingObservation.Missing, new StoreBackedCountrySettingSource(store).Read());
    }

    [Fact]
    public void AnUndefinedCountryPack_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StoreBackedCountrySettingSource(Open(), (CountryPack)99));
    }

    // ===== through the host and the owner =======================================================================================

    [Fact]
    public void AnIntactOff_StopsTheUsPack_AndKoreanAndGlobalDetectionStaysTheShippedOne()
    {
        const string korean = "연락처 010-1234-5678 이메일 user@example.test 901231-1234567 카드 4111 1111 1111 1111 서버 192.168.10.20";
        string Key(DetectionCandidate c) => $"{c.PiiType}|{c.Span}|{c.Canonical?.Value}|{c.RiskLevel}";
        var store = Open();
        store.SaveCountryChoice(Us, enabled: false, CountryChoiceOrigin.SettingsUserAction);
        using var owner = UsOptInOwner.CreateWithProductionAuthority(new StoreBackedCountrySettingSource(store));

        var pipeline = owner.CurrentPipeline();

        Assert.False(owner.CurrentReadiness().UsEnabled);
        Assert.Empty(UsFound(pipeline, UsText));
        Assert.Equal(
            DetectionPipeline.CreateDefault().Detect(korean).Candidates.OrderBy(c => c.Span.Start).Select(Key).ToList(),
            pipeline.Detect(korean).Candidates.OrderBy(c => c.Span.Start).Select(Key).ToList());
    }

    [Fact]
    public void OnThenOffThenOn_EachChangeIsServedAtOnce_AndAnOldOffPipelineIsNeverServedAgain()
    {
        var store = Open();
        using var owner = UsOptInOwner.CreateWithProductionAuthority(new StoreBackedCountrySettingSource(store));
        Assert.NotEmpty(UsFound(owner.CurrentPipeline(), UsText));                                 // no record: the pack is active

        store.SaveCountryChoice(Us, false, CountryChoiceOrigin.SettingsUserAction);
        var offPipeline = owner.CurrentPipeline();
        Assert.Empty(UsFound(offPipeline, UsText));
        Assert.Same(offPipeline, owner.CurrentPipeline());                                          // cached while the setting is unchanged

        store.SaveCountryChoice(Us, true, CountryChoiceOrigin.SettingsUserAction);
        var onPipeline = owner.CurrentPipeline();
        Assert.NotSame(offPipeline, onPipeline);                                                    // the OFF pipeline is gone
        Assert.NotEmpty(UsFound(onPipeline, UsText));
        Assert.True(owner.CurrentReadiness().UsEnabled);

        store.SaveCountryChoice(Us, false, CountryChoiceOrigin.SettingsUserAction);
        Assert.Empty(UsFound(owner.CurrentPipeline(), UsText));
        Assert.Equal(4, owner.BuildCount);
    }

    [Theory]
    [InlineData("tampered")]
    [InlineData("truncated")]
    [InlineData("unknown-version")]
    [InlineData("unknown-origin")]
    [InlineData("other-key")]
    [InlineData("wrong-purpose")]
    public void AnythingThatIsNotAnIntactOff_LeavesTheUsPackActive(string damage)
    {
        var store = Open();
        store.SaveCountryChoice(Us, enabled: false, CountryChoiceOrigin.SettingsUserAction);
        Assert.True(MasterKeyStore.TryLoad(Path.Combine(_root, "master.key"), out var key));
        byte[] Seal(string purpose, string payload) => SecureEnvelopeCodec.Encrypt(key, purpose, Encoding.UTF8.GetBytes(payload)).Encode();
        const string off = "{{\"FormatVersion\":{0},\"Revision\":5,\"Choices\":[{{\"Country\":\"UnitedStates\",\"Enabled\":false,\"Origin\":\"{1}\"}}]}}";

        switch (damage)
        {
            case "tampered":
                var bytes = File.ReadAllBytes(CountryFile);
                bytes[20] ^= 0xFF;
                File.WriteAllBytes(CountryFile, bytes);
                break;
            case "truncated": File.WriteAllBytes(CountryFile, File.ReadAllBytes(CountryFile)[..20]); break;
            case "unknown-version": File.WriteAllBytes(CountryFile, Seal(StorePurpose.CountrySettings, string.Format(off, 2, "SettingsUserAction"))); break;
            case "unknown-origin": File.WriteAllBytes(CountryFile, Seal(StorePurpose.CountrySettings, string.Format(off, 1, "Importer"))); break;
            case "wrong-purpose": File.WriteAllBytes(CountryFile, Seal(StorePurpose.Settings, string.Format(off, 1, "SettingsUserAction"))); break;
            case "other-key":
                File.WriteAllBytes(CountryFile, SecureEnvelopeCodec.Encrypt(new byte[32], StorePurpose.CountrySettings,
                    Encoding.UTF8.GetBytes(string.Format(off, 1, "SettingsUserAction"))).Encode());
                break;
        }

        using var owner = UsOptInOwner.CreateWithProductionAuthority(new StoreBackedCountrySettingSource(store));

        Assert.True(owner.CurrentReadiness().UsEnabled);
        Assert.NotEmpty(UsFound(owner.CurrentPipeline(), UsText));
    }

    [Fact]
    public void ARolledBackOff_IsNotReplayed_WhileTheProcessRuns()
    {
        var store = Open();
        store.SaveCountryChoice(Us, false, CountryChoiceOrigin.SettingsUserAction);            // revision 1: Off
        byte[] oldOff = File.ReadAllBytes(CountryFile);
        store.SaveCountryChoice(Us, true, CountryChoiceOrigin.SettingsUserAction);             // revision 2: On
        using var owner = UsOptInOwner.CreateWithProductionAuthority(new StoreBackedCountrySettingSource(store));
        Assert.NotEmpty(UsFound(owner.CurrentPipeline(), UsText));

        File.WriteAllBytes(CountryFile, oldOff);                                                // the old, valid Off is restored behind the process

        Assert.True(owner.CurrentReadiness().UsEnabled);
        Assert.NotEmpty(UsFound(owner.CurrentPipeline(), UsText));
    }

    [Fact]
    public void TheStoreBackedSource_ReadsOnlyTheCountryRecord_NotTheCategorySettings()
    {
        var store = Open();
        store.SaveSettings(new PrivonSettings(ProtectionEnabled: false, PausedUntilUtc: null,
            Categories: new ProtectionCategorySettings(false, false, false, false, false)));                // every category off, protection off
        var source = new StoreBackedCountrySettingSource(store);

        Assert.Equal(CountrySettingObservation.Missing, source.Read());                         // category toggles are not a country OFF
    }

    [Fact]
    public void ConcurrentChangesAndReads_NeverYieldAnOffUnlessAnOffWasRecordedLast()
    {
        var store = Open();
        var source = new StoreBackedCountrySettingSource(store);
        var seen = new System.Collections.Concurrent.ConcurrentBag<CountrySettingObservation>();
        using var stop = new CancellationTokenSource();
        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested) seen.Add(source.Read());
        })).ToList();

        for (int i = 0; i < 60; i++)
        {
            for (int attempt = 0; attempt < 100; attempt++)
            {
                try { store.SaveCountryChoice(Us, enabled: i % 2 == 1, CountryChoiceOrigin.SettingsUserAction); break; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Thread.Sleep(2); }   // a reader held the file; the old record was kept
            }
        }

        stop.Cancel();
        Task.WaitAll(readers.ToArray());

        // damaged or newer data is never observed, and the final read is the last recorded choice (59 is odd -> On)
        Assert.DoesNotContain(seen, o => o is CountrySettingObservation.Corrupt or CountrySettingObservation.UnknownVersion);
        Assert.Equal(CountrySettingObservation.AuthenticatedOn, source.Read());
    }
}
