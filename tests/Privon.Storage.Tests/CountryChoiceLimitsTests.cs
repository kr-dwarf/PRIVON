using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using Privon.Storage;

namespace Privon.Storage.Tests;

// SYNTHETIC DATA ONLY. These tests do NOT show protection; they pin the LIMITS of the country-choice record, so that nobody reads the store as more than
// it is. Each "Limitation_" test asserts what the design cannot prevent today -- if one ever fails, a limitation was removed on purpose or by accident, and
// the report that lists it as open must be revisited.
public sealed class CountryChoiceLimitsTests : IDisposable
{
    private const string Us = "UnitedStates";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CountryChoiceLimits", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string CountryFile => Path.Combine(_root, "country-settings.bin");

    // A new process starts with no memory of the revisions the old one saw: this clears the in-process rollback guard to model exactly that.
    private void SimulateProcessRestart()
    {
        var field = typeof(PrivonLocalStore).GetField("CountryHighestRevision", BindingFlags.NonPublic | BindingFlags.Static)!;
        var map = (ConcurrentDictionary<string, long>)field.GetValue(null)!;
        map.TryRemove(CountryFile, out _);
        foreach (var key in map.Keys.Where(k => string.Equals(k, CountryFile, StringComparison.OrdinalIgnoreCase)).ToList()) map.TryRemove(key, out _);
    }

    [Fact]
    public void Limitation_AnOlderValidRecordRestoredWhilePrivonIsNotRunning_IsAcceptedAfterTheRestart()
    {
        var store = PrivonLocalStore.OpenOrCreate(_root);
        store.SaveCountryChoice(Us, enabled: false, CountryChoiceOrigin.SettingsUserAction);       // revision 1: Off
        byte[] oldOff = File.ReadAllBytes(CountryFile);
        store.SaveCountryChoice(Us, enabled: true, CountryChoiceOrigin.SettingsUserAction);        // revision 2: On, the user's latest choice

        File.WriteAllBytes(CountryFile, oldOff);                                                    // the old record is put back while PRIVON is closed
        SimulateProcessRestart();

        // Nothing outside the user's own files can say the record is stale, so the old Off is read as the choice. The in-process guard (tested in
        // CountryChoiceStoreTests) does not survive a restart. UNRESOLVED -- closing this needs state the user's own account cannot rewrite.
        var afterRestart = PrivonLocalStore.OpenOrCreate(_root).LoadCountryChoice(Us);
        Assert.Equal(CountryChoiceState.Off, afterRestart.State);
        Assert.Equal(1, afterRestart.Revision);
    }

    [Fact]
    public void Limitation_CodeRunningAsTheSameWindowsUser_CanWriteAValidOff_AndItReadsAsAChoice()
    {
        PrivonLocalStore.OpenOrCreate(_root);                                                       // creates the user's master key

        // any process of this user can unprotect the key with DPAPI (CurrentUser scope) and seal a record the store accepts
        Assert.True(MasterKeyStore.TryLoad(Path.Combine(_root, "master.key"), out var key));
        var forged = SecureEnvelopeCodec.Encrypt(key, StorePurpose.CountrySettings, Encoding.UTF8.GetBytes(
            "{\"FormatVersion\":1,\"Revision\":1,\"Choices\":[{\"Country\":\"UnitedStates\",\"Enabled\":false,\"Origin\":\"SettingsUserAction\"}]}"));
        AtomicFileWriter.WriteAllBytes(CountryFile, forged.Encode());

        // The record is intact and carries the origin marker, which is all the store can check. It is not evidence that a person chose anything.
        Assert.Equal(CountryChoiceState.Off, PrivonLocalStore.OpenOrCreate(_root).LoadCountryChoice(Us).State);
    }

    [Fact]
    public void Limitation_AnotherUsersRecord_CannotBeAuthenticated_ButOnlyBecauseTheirKeyIsNotThisUsers()
    {
        // The only per-user protection is the key: a record sealed under a different key never authenticates. (A real second Windows account would
        // additionally be unable to unprotect this user's master.key; that cross-account case needs a second logon session and is not run here.)
        string other = Path.Combine(_root, "other");
        PrivonLocalStore.OpenOrCreate(other).SaveCountryChoice(Us, enabled: false, CountryChoiceOrigin.SettingsUserAction);
        string mine = Path.Combine(_root, "mine");
        var store = PrivonLocalStore.OpenOrCreate(mine);
        File.Copy(Path.Combine(other, "country-settings.bin"), Path.Combine(mine, "country-settings.bin"));

        Assert.Equal(CountryChoiceState.Unauthenticated, store.LoadCountryChoice(Us).State);
    }

    [Fact]
    public void ASavedChoice_SurvivesTheProcessEndingRightAfterTheSave()
    {
        var store = PrivonLocalStore.OpenOrCreate(_root);

        store.SaveCountryChoice(Us, enabled: false, CountryChoiceOrigin.SettingsUserAction);
        // the process ends here: nothing is flushed or disposed afterwards. A later run sees the record, whole, with no temporary file left behind.
        SimulateProcessRestart();

        var later = PrivonLocalStore.OpenOrCreate(_root).LoadCountryChoice(Us);
        Assert.Equal(CountryChoiceState.Off, later.State);
        Assert.Empty(Directory.GetFiles(_root, ".country-settings.bin.*.tmp"));
    }

    [Fact]
    public void AWriteErrorInTheMiddleOfAChangeSequence_KeepsTheLastVerifiedChoice_AndTheSequenceContinues()
    {
        var store = PrivonLocalStore.OpenOrCreate(_root);
        store.SaveCountryChoice(Us, enabled: true, CountryChoiceOrigin.SettingsUserAction);
        var states = new List<CountryChoiceState>();

        using (new FileStream(CountryFile, FileMode.Open, FileAccess.Read, FileShare.Read))        // the second change cannot be written
        {
            Assert.True(Record.Exception(() => store.SaveCountryChoice(Us, enabled: false, CountryChoiceOrigin.SettingsUserAction)) is not null);
            states.Add(store.LoadCountryChoice(Us).State);
        }

        store.SaveCountryChoice(Us, enabled: true, CountryChoiceOrigin.SettingsUserAction);
        states.Add(store.LoadCountryChoice(Us).State);
        store.SaveCountryChoice(Us, enabled: false, CountryChoiceOrigin.SettingsUserAction);
        states.Add(store.LoadCountryChoice(Us).State);

        Assert.Equal([CountryChoiceState.On, CountryChoiceState.On, CountryChoiceState.Off], states);   // never a half state, never the failed Off
    }
}
