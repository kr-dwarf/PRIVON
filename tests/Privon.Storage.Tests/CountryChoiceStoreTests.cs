using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Privon.Storage;

namespace Privon.Storage.Tests;

// SYNTHETIC DATA ONLY. The T-1 storage foundation: a per-country ON/OFF record in its own file, intact only under this Windows user's key and this
// purpose. Every test that writes a choice calls SaveCountryChoice directly -- that stands in for a Settings user action the product does NOT have yet,
// so what these tests prove is the record's behaviour (integrity, scope, versioning, recovery), never that a person chose anything.
public sealed class CountryChoiceStoreTests : IDisposable
{
    private const string Us = "UnitedStates";
    private readonly List<string> _roots = [];

    public void Dispose()
    {
        foreach (var r in _roots)
        {
            try { Directory.Delete(r, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "CountryChoice", Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        return root;
    }

    private static string FileOf(string root) => Path.Combine(root, "country-settings.bin");

    private static byte[] KeyOf(string root)
    {
        Assert.True(MasterKeyStore.TryLoad(Path.Combine(root, "master.key"), out var key));
        return key;
    }

    private static string Payload(long revision, string country, bool enabled, string origin = "SettingsUserAction", int version = 1) =>
        $"{{\"FormatVersion\":{version},\"Revision\":{revision},\"Choices\":[{{\"Country\":\"{country}\",\"Enabled\":{enabled.ToString().ToLowerInvariant()},\"Origin\":\"{origin}\"}}]}}";

    // a record that is authentic by every means the store has (this user's key, the right purpose) but carries whatever payload the test chooses
    private static void WriteRecord(string root, string payload, string purpose = StorePurpose.CountrySettings)
    {
        var envelope = SecureEnvelopeCodec.Encrypt(KeyOf(root), purpose, Encoding.UTF8.GetBytes(payload));
        AtomicFileWriter.WriteAllBytes(FileOf(root), envelope.Encode());
    }

    // ===== persistence ==========================================================================================================

    [Fact]
    public void WithNoRecord_TheChoiceIsMissing_ForEveryCountry()
    {
        var store = PrivonLocalStore.OpenOrCreate(NewRoot());

        Assert.Equal(CountryChoiceState.Missing, store.LoadCountryChoice(Us).State);
        Assert.Equal(CountryChoiceState.Missing, store.LoadCountryChoice("Korea").State);
    }

    [Fact]
    public void AnOffChoice_IsStored_AndReadBackByTheSameAndByALaterRun()
    {
        string root = NewRoot();
        var store = PrivonLocalStore.OpenOrCreate(root);

        store.SaveCountryChoice(Us, enabled: false, CountryChoiceOrigin.SettingsUserAction);

        Assert.Equal(CountryChoiceState.Off, store.LoadCountryChoice(Us).State);
        Assert.Equal(CountryChoiceState.Off, PrivonLocalStore.OpenOrCreate(root).LoadCountryChoice(Us).State);   // a later run of the same user
    }

    [Fact]
    public void AnOnChoice_IsStored_AndReadBack()
    {
        string root = NewRoot();
        var store = PrivonLocalStore.OpenOrCreate(root);

        store.SaveCountryChoice(Us, enabled: true, CountryChoiceOrigin.SettingsUserAction);

        Assert.Equal(CountryChoiceState.On, PrivonLocalStore.OpenOrCreate(root).LoadCountryChoice(Us).State);
    }

    [Fact]
    public void OnThenOffThenOn_EachChangeIsTheNewState_AndTheRevisionOnlyGoesUp()
    {
        var store = PrivonLocalStore.OpenOrCreate(NewRoot());
        var seen = new List<(CountryChoiceState State, long Revision)>();

        foreach (bool enabled in new[] { true, false, true, false, true })
        {
            store.SaveCountryChoice(Us, enabled, CountryChoiceOrigin.SettingsUserAction);
            var read = store.LoadCountryChoice(Us);
            seen.Add((read.State, read.Revision));
        }

        Assert.Equal([CountryChoiceState.On, CountryChoiceState.Off, CountryChoiceState.On, CountryChoiceState.Off, CountryChoiceState.On],
            seen.Select(s => s.State));
        Assert.Equal([1L, 2, 3, 4, 5], seen.Select(s => s.Revision));
    }

    [Fact]
    public void CountriesAreIndependent_OneCountrysChoiceNeverChangesAnothers()
    {
        var store = PrivonLocalStore.OpenOrCreate(NewRoot());

        store.SaveCountryChoice(Us, false, CountryChoiceOrigin.SettingsUserAction);
        Assert.Equal(CountryChoiceState.Missing, store.LoadCountryChoice("Korea").State);

        store.SaveCountryChoice("Korea", true, CountryChoiceOrigin.SettingsUserAction);
        store.SaveCountryChoice(Us, true, CountryChoiceOrigin.SettingsUserAction);

        Assert.Equal(CountryChoiceState.On, store.LoadCountryChoice(Us).State);
        Assert.Equal(CountryChoiceState.On, store.LoadCountryChoice("Korea").State);
        store.SaveCountryChoice(Us, false, CountryChoiceOrigin.SettingsUserAction);
        Assert.Equal(CountryChoiceState.Off, store.LoadCountryChoice(Us).State);
        Assert.Equal(CountryChoiceState.On, store.LoadCountryChoice("Korea").State);   // untouched
    }

    [Fact]
    public void ABadCountryIdOrAnUnknownOrigin_IsRefused_AndNothingIsWritten()
    {
        string root = NewRoot();
        var store = PrivonLocalStore.OpenOrCreate(root);

        foreach (string bad in new[] { "", " ", "1Us", "United States", "us/off", "..", new string('a', 33), "Üs" })
        {
            Assert.Throws<ArgumentException>(() => store.LoadCountryChoice(bad));
            Assert.Throws<ArgumentException>(() => store.SaveCountryChoice(bad, false, CountryChoiceOrigin.SettingsUserAction));
        }

        Assert.Throws<ArgumentException>(() => store.SaveCountryChoice(Us, false, CountryChoiceOrigin.Unknown));
        Assert.Throws<ArgumentException>(() => store.SaveCountryChoice(Us, false, (CountryChoiceOrigin)7));
        Assert.False(File.Exists(FileOf(root)));
    }

    // ===== integrity: only an intact record is a choice =========================================================================

    [Fact]
    public void ATamperedCiphertext_IsNeverAnOff_ItFailsAuthentication()
    {
        string root = NewRoot();
        var store = PrivonLocalStore.OpenOrCreate(root);
        store.SaveCountryChoice(Us, false, CountryChoiceOrigin.SettingsUserAction);
        byte[] bytes = File.ReadAllBytes(FileOf(root));

        foreach (int index in new[] { 1, 13, 29, bytes.Length - 1 })   // nonce, tag, ciphertext
        {
            var tampered = (byte[])bytes.Clone();
            tampered[index] ^= 0x01;
            File.WriteAllBytes(FileOf(root), tampered);

            Assert.Equal(CountryChoiceState.Unauthenticated, store.LoadCountryChoice(Us).State);
        }
    }

    [Fact]
    public void ATruncatedOrGarbageFile_IsCorrupt_NeverAChoice()
    {
        string root = NewRoot();
        var store = PrivonLocalStore.OpenOrCreate(root);
        store.SaveCountryChoice(Us, false, CountryChoiceOrigin.SettingsUserAction);
        byte[] bytes = File.ReadAllBytes(FileOf(root));

        File.WriteAllBytes(FileOf(root), bytes[..10]);
        Assert.Equal(CountryChoiceState.Corrupt, store.LoadCountryChoice(Us).State);

        File.WriteAllBytes(FileOf(root), []);
        Assert.Equal(CountryChoiceState.Corrupt, store.LoadCountryChoice(Us).State);

        File.WriteAllBytes(FileOf(root), Encoding.UTF8.GetBytes("{\"UnitedStates\":\"Off\"}"));
        Assert.Equal(CountryChoiceState.Corrupt, store.LoadCountryChoice(Us).State);
    }

    [Fact]
    public void ARecordEncryptedForAnotherPurpose_OrCopiedFromSettings_IsNotAnAuthenticatedChoice()
    {
        string root = NewRoot();
        var store = PrivonLocalStore.OpenOrCreate(root);

        WriteRecord(root, Payload(1, Us, false), purpose: StorePurpose.Settings);              // right key, wrong purpose binding
        Assert.Equal(CountryChoiceState.Unauthenticated, store.LoadCountryChoice(Us).State);

        store.SaveSettings(PrivonSettings.SafeDefault);                                           // a genuine settings.bin copied into place
        File.Copy(Path.Combine(root, "settings.bin"), FileOf(root), overwrite: true);
        Assert.Equal(CountryChoiceState.Unauthenticated, store.LoadCountryChoice(Us).State);
    }

    [Fact]
    public void ARecordFromAnotherKey_IsNotAnAuthenticatedChoice()
    {
        // The protection scope in miniature: a record sealed under someone else's master key cannot be authenticated with this user's key.
        string theirs = NewRoot();
        PrivonLocalStore.OpenOrCreate(theirs).SaveCountryChoice(Us, false, CountryChoiceOrigin.SettingsUserAction);
        string mine = NewRoot();
        var store = PrivonLocalStore.OpenOrCreate(mine);
        File.Copy(FileOf(theirs), FileOf(mine));

        Assert.Equal(CountryChoiceState.Unauthenticated, store.LoadCountryChoice(Us).State);
    }

    [Fact]
    public void WithoutAUsableMasterKey_NothingIsAnAuthenticatedChoice_AndNothingIsWritten()
    {
        string root = NewRoot();
        PrivonLocalStore.OpenOrCreate(root).SaveCountryChoice(Us, false, CountryChoiceOrigin.SettingsUserAction);
        byte[] before = File.ReadAllBytes(FileOf(root));
        using var held = new FileStream(Path.Combine(root, "master.key"), FileMode.Open, FileAccess.Read, FileShare.None);
        var locked = PrivonLocalStore.OpenOrCreate(root);

        Assert.True(locked.IsMasterKeyUnavailable);
        Assert.Equal(CountryChoiceState.Unauthenticated, locked.LoadCountryChoice(Us).State);
        Assert.Throws<InvalidOperationException>(() => locked.SaveCountryChoice(Us, true, CountryChoiceOrigin.SettingsUserAction));
        Assert.Equal(before, File.ReadAllBytes(FileOf(root)));                                    // the record is exactly as it was
    }

    [Theory]
    [InlineData("{\"FormatVersion\":1,\"Revision\":0,\"Choices\":[]}")]                                                                // revision < 1
    [InlineData("{\"FormatVersion\":1,\"Revision\":1}")]                                                                                // no choices array
    [InlineData("{\"FormatVersion\":1,\"Revision\":1,\"Choices\":[{\"Country\":\"UnitedStates\",\"Enabled\":\"false\",\"Origin\":\"SettingsUserAction\"}]}")]   // not a boolean
    [InlineData("{\"FormatVersion\":1,\"Revision\":1,\"Choices\":[{\"Country\":\"../x\",\"Enabled\":false,\"Origin\":\"SettingsUserAction\"}]}")]
    [InlineData("{\"FormatVersion\":1,\"Revision\":1,\"Choices\":[{\"Country\":\"UnitedStates\",\"Enabled\":false,\"Origin\":\"SettingsUserAction\"},{\"Country\":\"unitedstates\",\"Enabled\":true,\"Origin\":\"SettingsUserAction\"}]}")]  // duplicates
    [InlineData("{\"FormatVersion\":0,\"Revision\":1,\"Choices\":[]}")]
    [InlineData("[1,2,3]")]
    [InlineData("not json at all")]
    public void AnAuthenticButMalformedPayload_IsCorrupt_NeverAChoice(string payload)
    {
        string root = NewRoot();
        var store = PrivonLocalStore.OpenOrCreate(root);
        WriteRecord(root, payload);

        Assert.Equal(CountryChoiceState.Corrupt, store.LoadCountryChoice(Us).State);
    }

    [Fact]
    public void AnUnknownOrMissingOrigin_IsUnauthenticated_NeverAnOff()
    {
        string root = NewRoot();
        var store = PrivonLocalStore.OpenOrCreate(root);

        foreach (string origin in new[] { "Test", "", "settingsuseraction", "Importer", "SettingsUserAction " })
        {
            WriteRecord(root, Payload(1, Us, enabled: false, origin));
            Assert.Equal(CountryChoiceState.Unauthenticated, store.LoadCountryChoice(Us).State);
        }
    }

    // ===== versions and legacy data =============================================================================================

    [Fact]
    public void ANewerFormatVersion_IsUnknown_NeverInterpreted_AndNeverOverwritten()
    {
        string root = NewRoot();
        var store = PrivonLocalStore.OpenOrCreate(root);
        WriteRecord(root, Payload(9, Us, enabled: false, version: 2));
        byte[] before = File.ReadAllBytes(FileOf(root));

        Assert.Equal(CountryChoiceState.UnknownVersion, store.LoadCountryChoice(Us).State);
        Assert.Throws<InvalidOperationException>(() => store.SaveCountryChoice(Us, true, CountryChoiceOrigin.SettingsUserAction));
        Assert.Equal(before, File.ReadAllBytes(FileOf(root)));                                    // a newer build's data is never destroyed
        Assert.Equal(CountryChoiceState.UnknownVersion, PrivonLocalStore.OpenOrCreate(root).LoadCountryChoice(Us).State);
    }

    [Fact]
    public void AnUnknownEnvelopeVersion_IsNotAChoice()
    {
        string root = NewRoot();
        var store = PrivonLocalStore.OpenOrCreate(root);
        store.SaveCountryChoice(Us, false, CountryChoiceOrigin.SettingsUserAction);
        byte[] bytes = File.ReadAllBytes(FileOf(root));
        bytes[0] = 9;
        File.WriteAllBytes(FileOf(root), bytes);

        Assert.NotEqual(CountryChoiceState.Off, store.LoadCountryChoice(Us).State);
        Assert.NotEqual(CountryChoiceState.On, store.LoadCountryChoice(Us).State);
    }

    [Fact]
    public void LegacySettings_AreLoadedAsBefore_AndNeitherFileEverTouchesTheOther()
    {
        string root = NewRoot();
        var store = PrivonLocalStore.OpenOrCreate(root);
        // a settings file from before categories existed: no Categories property at all
        var legacy = SecureEnvelopeCodec.Encrypt(KeyOf(root), StorePurpose.Settings, Encoding.UTF8.GetBytes("{\"ProtectionEnabled\":false,\"PausedUntilUtc\":null}"));
        AtomicFileWriter.WriteAllBytes(Path.Combine(root, "settings.bin"), legacy.Encode());
        byte[] settingsBefore = File.ReadAllBytes(Path.Combine(root, "settings.bin"));

        var loaded = store.LoadSettings();
        store.SaveCountryChoice(Us, false, CountryChoiceOrigin.SettingsUserAction);
        byte[] countryBefore = File.ReadAllBytes(FileOf(root));

        Assert.False(loaded.ProtectionEnabled);
        Assert.Equal(ProtectionCategorySettings.AllOn, loaded.Categories);                       // the legacy upgrade path is unchanged
        Assert.Equal(settingsBefore, File.ReadAllBytes(Path.Combine(root, "settings.bin")));       // writing a country choice leaves settings.bin byte-identical
        store.SaveSettings(PrivonSettings.SafeDefault);
        Assert.Equal(countryBefore, File.ReadAllBytes(FileOf(root)));                             // and saving settings leaves the country record byte-identical
        Assert.Equal(CountryChoiceState.Off, store.LoadCountryChoice(Us).State);
    }

    [Fact]
    public void PrivonSettings_StillHasNoCountryField_TheChoiceLivesOnlyInItsOwnRecord()
    {
        Assert.Equal(["Categories", "PausedUntilUtc", "ProtectionEnabled"],
            typeof(PrivonSettings).GetProperties().Where(p => p.Name != "EqualityContract" && p.Name != "SafeDefault").Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    // ===== failure and recovery =================================================================================================

    [Fact]
    public void AFailedWrite_LeavesTheExistingRecordAndNoTemporaryFile_AndTheNextWriteSucceeds()
    {
        string root = NewRoot();
        var store = PrivonLocalStore.OpenOrCreate(root);
        store.SaveCountryChoice(Us, true, CountryChoiceOrigin.SettingsUserAction);
        byte[] before = File.ReadAllBytes(FileOf(root));

        using (var held = new FileStream(FileOf(root), FileMode.Open, FileAccess.Read, FileShare.Read))   // readable, but cannot be replaced
        {
            // replacing a file another handle holds fails with an IO or access error (Windows reports it as either); the old record is kept
            var failure = Record.Exception(() => store.SaveCountryChoice(Us, false, CountryChoiceOrigin.SettingsUserAction));
            Assert.True(failure is IOException or UnauthorizedAccessException, failure?.GetType().Name ?? "no exception");
        }

        Assert.Equal(before, File.ReadAllBytes(FileOf(root)));
        Assert.Equal(CountryChoiceState.On, store.LoadCountryChoice(Us).State);                   // the old, good state still stands
        Assert.Empty(Directory.GetFiles(root, ".country-settings.bin.*.tmp"));

        store.SaveCountryChoice(Us, false, CountryChoiceOrigin.SettingsUserAction);                // and it recovers once the file is free
        Assert.Equal(CountryChoiceState.Off, store.LoadCountryChoice(Us).State);
    }

    [Fact]
    public void ARecordIsReplacedViaATemporaryFile_NeverRewrittenInPlace()
    {
        // The atomic writer builds the new record in a temporary file next to the old one and moves it into place; an in-place rewrite would create
        // no such file. So watching the directory for that temporary file is what tells the two apart (the old record stays whole until the move).
        string root = NewRoot();
        var store = PrivonLocalStore.OpenOrCreate(root);
        store.SaveCountryChoice(Us, true, CountryChoiceOrigin.SettingsUserAction);
        using var created = new ManualResetEventSlim();
        using var watcher = new FileSystemWatcher(root, ".country-settings.bin.*.tmp") { NotifyFilter = NotifyFilters.FileName, EnableRaisingEvents = true };
        watcher.Created += (_, _) => created.Set();
        watcher.Renamed += (_, _) => created.Set();

        store.SaveCountryChoice(Us, false, CountryChoiceOrigin.SettingsUserAction);

        Assert.True(created.Wait(TimeSpan.FromSeconds(5)), "no temporary file was used: the record was rewritten in place");
        Assert.Empty(Directory.GetFiles(root, ".country-settings.bin.*.tmp"));                   // and it is gone once the record is in place
        Assert.Equal(CountryChoiceState.Off, store.LoadCountryChoice(Us).State);
    }

    [Fact]
    public void AnInterruptedWriteLeavesOnlyAStrayTemporaryFile_WhichChangesNothing()
    {
        string root = NewRoot();
        var store = PrivonLocalStore.OpenOrCreate(root);
        store.SaveCountryChoice(Us, true, CountryChoiceOrigin.SettingsUserAction);
        File.WriteAllBytes(Path.Combine(root, $".country-settings.bin.{Guid.NewGuid():N}.tmp"), [1, 2, 3, 4]);   // what a killed process could leave behind

        Assert.Equal(CountryChoiceState.On, store.LoadCountryChoice(Us).State);
        store.SaveCountryChoice(Us, false, CountryChoiceOrigin.SettingsUserAction);
        Assert.Equal(CountryChoiceState.Off, PrivonLocalStore.OpenOrCreate(root).LoadCountryChoice(Us).State);
    }

    [Fact]
    public void ADamagedRecord_IsReplacedByTheNextChoice_WithoutManualRepair()
    {
        string root = NewRoot();
        var store = PrivonLocalStore.OpenOrCreate(root);
        store.SaveCountryChoice(Us, false, CountryChoiceOrigin.SettingsUserAction);
        File.WriteAllBytes(FileOf(root), [0, 1, 2]);
        Assert.Equal(CountryChoiceState.Corrupt, store.LoadCountryChoice(Us).State);

        store.SaveCountryChoice(Us, true, CountryChoiceOrigin.SettingsUserAction);

        Assert.Equal(CountryChoiceState.On, store.LoadCountryChoice(Us).State);
    }

    [Fact]
    public void AnUnreadableRecord_IsNeverReplacedBlindly()
    {
        string root = NewRoot();
        var store = PrivonLocalStore.OpenOrCreate(root);
        store.SaveCountryChoice(Us, false, CountryChoiceOrigin.SettingsUserAction);
        using var held = new FileStream(FileOf(root), FileMode.Open, FileAccess.Read, FileShare.None);   // present, but not readable right now

        Assert.Equal(CountryChoiceState.Unauthenticated, store.LoadCountryChoice(Us).State);
        Assert.ThrowsAny<IOException>(() => store.SaveCountryChoice(Us, true, CountryChoiceOrigin.SettingsUserAction));
    }

    // ===== rollback =============================================================================================================

    [Fact]
    public void ARecordRestoredToAnOlderRevision_IsNotAChoice_WhileThisProcessRuns_AndTheNextChoiceRepairsIt()
    {
        string root = NewRoot();
        var store = PrivonLocalStore.OpenOrCreate(root);
        store.SaveCountryChoice(Us, false, CountryChoiceOrigin.SettingsUserAction);            // revision 1: Off
        byte[] old = File.ReadAllBytes(FileOf(root));
        store.SaveCountryChoice(Us, true, CountryChoiceOrigin.SettingsUserAction);             // revision 2: On
        Assert.Equal(CountryChoiceState.On, store.LoadCountryChoice(Us).State);

        File.WriteAllBytes(FileOf(root), old);                                                  // an old, perfectly valid Off is put back

        Assert.Equal(CountryChoiceState.Unauthenticated, store.LoadCountryChoice(Us).State);   // never replayed as an Off
        store.SaveCountryChoice(Us, true, CountryChoiceOrigin.SettingsUserAction);
        var repaired = store.LoadCountryChoice(Us);
        Assert.Equal(CountryChoiceState.On, repaired.State);
        Assert.True(repaired.Revision > 2);
    }

    // ===== concurrency ==========================================================================================================

    [Fact]
    public void ConcurrentWritersAndReaders_NeverSeeAHalfWrittenRecord_AndEveryWriteCounts()
    {
        string root = NewRoot();
        var store = PrivonLocalStore.OpenOrCreate(root);
        store.SaveCountryChoice(Us, true, CountryChoiceOrigin.SettingsUserAction);
        var bad = new List<CountryChoiceState>();
        using var stop = new CancellationTokenSource();

        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var state = store.LoadCountryChoice(Us).State;
                // On / Off while intact; Unauthenticated only while the OS has the file busy during an atomic replace. Never damaged, never newer.
                if (state is CountryChoiceState.Corrupt or CountryChoiceState.UnknownVersion or CountryChoiceState.Missing) lock (bad) bad.Add(state);
            }
        })).ToList();

        int writes = 0;
        Parallel.For(0, 120, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
        {
            for (int attempt = 0; attempt < 50; attempt++)
            {
                try
                {
                    store.SaveCountryChoice(Us, i % 2 == 0, CountryChoiceOrigin.SettingsUserAction);
                    Interlocked.Increment(ref writes);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(2);   // a reader held the file for a moment; the old record was kept
                }
            }
        });
        stop.Cancel();
        Task.WaitAll(readers.ToArray());

        Assert.Empty(bad);
        var final = store.LoadCountryChoice(Us);
        Assert.True(final.State is CountryChoiceState.On or CountryChoiceState.Off);
        Assert.Equal(1 + writes, final.Revision);                                              // every completed write advanced the revision by exactly one
    }
}
