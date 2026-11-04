using System.Text;
using Privon.Storage;

namespace Privon.Storage.Tests;

// SYNTHETIC DATA ONLY. The P1 activation record: "the user explicitly turned a country's detection on", in its own file and purpose, on the same envelope /
// document / origin / revision / atomic-replace machinery as the country choice. Tests call SaveCountryActivation directly, which stands in for a Settings user
// action; what they prove is the record's behaviour (integrity, separation, versioning, recovery), never that a person chose anything.
public sealed class CountryActivationStoreTests : IDisposable
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
        string root = Path.Combine(Path.GetTempPath(), "CountryActivation", Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        return root;
    }

    private static string ActivationFile(string root) => Path.Combine(root, "country-activation.bin");

    private static string ChoiceFile(string root) => Path.Combine(root, "country-settings.bin");

    private static byte[] KeyOf(string root)
    {
        Assert.True(MasterKeyStore.TryLoad(Path.Combine(root, "master.key"), out var key));
        return key;
    }

    private static string Payload(long revision, string country, bool enabled, string origin = "SettingsUserAction", int version = 1) =>
        $"{{\"FormatVersion\":{version},\"Revision\":{revision},\"Choices\":[{{\"Country\":\"{country}\",\"Enabled\":{enabled.ToString().ToLowerInvariant()},\"Origin\":\"{origin}\"}}]}}";

    private static void WriteActivationRecord(string root, string payload, string purpose = StorePurpose.CountryActivation)
    {
        var envelope = SecureEnvelopeCodec.Encrypt(KeyOf(root), purpose, Encoding.UTF8.GetBytes(payload));
        AtomicFileWriter.WriteAllBytes(ActivationFile(root), envelope.Encode());
    }

    [Fact]
    public void NeverActivated_ReadsMissing_AndNothingIsCreatedByLooking()
    {
        string root = NewRoot();
        var store = PrivonLocalStore.OpenOrCreate(root);

        Assert.Equal(CountryChoiceState.Missing, store.LoadCountryActivation(Us).State);
        Assert.False(File.Exists(ActivationFile(root)));
    }

    [Fact]
    public void AnActivation_IsStored_ReadBackAsOn_AndSurvivesALaterRun()
    {
        string root = NewRoot();
        var store = PrivonLocalStore.OpenOrCreate(root);

        store.SaveCountryActivation(Us, CountryChoiceOrigin.SettingsUserAction);

        Assert.Equal(CountryChoiceState.On, store.LoadCountryActivation(Us).State);
        Assert.Equal(CountryChoiceState.On, PrivonLocalStore.OpenOrCreate(root).LoadCountryActivation(Us).State);
    }

    [Fact]
    public void TheActivationAndTheCountryChoice_AreSeparateRecords_NeitherWritesTheOther()
    {
        string root = NewRoot();
        var store = PrivonLocalStore.OpenOrCreate(root);

        store.SaveCountryChoice(Us, enabled: false, CountryChoiceOrigin.SettingsUserAction);
        Assert.Equal(CountryChoiceState.Missing, store.LoadCountryActivation(Us).State);     // an Off choice is NOT an activation
        Assert.False(File.Exists(ActivationFile(root)));

        store.SaveCountryActivation(Us, CountryChoiceOrigin.SettingsUserAction);
        Assert.Equal(CountryChoiceState.Off, store.LoadCountryChoice(Us).State);              // and an activation leaves the choice as it was
        Assert.Equal(CountryChoiceState.On, store.LoadCountryActivation(Us).State);
    }

    [Fact]
    public void ADamagedChoiceFile_SaysNothingAboutTheActivation_AndTheReverse()
    {
        string root = NewRoot();
        var store = PrivonLocalStore.OpenOrCreate(root);
        store.SaveCountryChoice(Us, enabled: false, CountryChoiceOrigin.SettingsUserAction);
        store.SaveCountryActivation(Us, CountryChoiceOrigin.SettingsUserAction);

        File.WriteAllBytes(ChoiceFile(root), [1, 2, 3]);
        Assert.Equal(CountryChoiceState.Corrupt, store.LoadCountryChoice(Us).State);
        Assert.Equal(CountryChoiceState.On, store.LoadCountryActivation(Us).State);           // the activation is intact whatever the choice file looks like

        File.WriteAllBytes(ActivationFile(root), [9, 9, 9]);
        Assert.Equal(CountryChoiceState.Corrupt, store.LoadCountryActivation(Us).State);
    }

    [Fact]
    public void AGarbageOrTamperedActivation_IsNeverOn_AndNeverMissing()
    {
        string root = NewRoot();
        var store = PrivonLocalStore.OpenOrCreate(root);
        store.SaveCountryActivation(Us, CountryChoiceOrigin.SettingsUserAction);
        byte[] intact = File.ReadAllBytes(ActivationFile(root));

        File.WriteAllBytes(ActivationFile(root), [1, 2, 3]);
        Assert.Equal(CountryChoiceState.Corrupt, store.LoadCountryActivation(Us).State);

        byte[] tampered = (byte[])intact.Clone();
        tampered[^1] ^= 1;
        File.WriteAllBytes(ActivationFile(root), tampered);
        Assert.Equal(CountryChoiceState.Unauthenticated, store.LoadCountryActivation(Us).State);
    }

    [Fact]
    public void ACountryChoiceFileCopiedOverTheActivation_IsNotAnActivation_ItIsBoundToItsOwnPurpose()
    {
        string root = NewRoot();
        var store = PrivonLocalStore.OpenOrCreate(root);
        store.SaveCountryChoice(Us, enabled: true, CountryChoiceOrigin.SettingsUserAction);

        File.Copy(ChoiceFile(root), ActivationFile(root));

        Assert.Equal(CountryChoiceState.Unauthenticated, store.LoadCountryActivation(Us).State);
    }

    [Fact]
    public void AnAuthenticButUnknownOrigin_IsUnauthenticated_NeverAnActivation()
    {
        string root = NewRoot();
        var store = PrivonLocalStore.OpenOrCreate(root);
        WriteActivationRecord(root, Payload(1, Us, true, origin: "SomethingElse"));

        Assert.Equal(CountryChoiceState.Unauthenticated, store.LoadCountryActivation(Us).State);
    }

    [Fact]
    public void ANewerFormatVersion_IsUnknown_NeverInterpreted_AndNeverOverwritten()
    {
        string root = NewRoot();
        var store = PrivonLocalStore.OpenOrCreate(root);
        WriteActivationRecord(root, Payload(1, Us, true, version: 2));
        byte[] before = File.ReadAllBytes(ActivationFile(root));

        Assert.Equal(CountryChoiceState.UnknownVersion, store.LoadCountryActivation(Us).State);
        Assert.Throws<InvalidOperationException>(() => store.SaveCountryActivation(Us, CountryChoiceOrigin.SettingsUserAction));
        Assert.Equal(before, File.ReadAllBytes(ActivationFile(root)));
    }

    [Fact]
    public void ABadCountryIdOrAnUnknownOrigin_IsRefused_AndNothingIsWritten()
    {
        string root = NewRoot();
        var store = PrivonLocalStore.OpenOrCreate(root);

        Assert.Throws<ArgumentException>(() => store.SaveCountryActivation("not valid!", CountryChoiceOrigin.SettingsUserAction));
        Assert.Throws<ArgumentException>(() => store.SaveCountryActivation(Us, CountryChoiceOrigin.Unknown));
        Assert.Throws<ArgumentException>(() => store.LoadCountryActivation("1bad"));
        Assert.False(File.Exists(ActivationFile(root)));
    }

    [Fact]
    public void WithoutAUsableMasterKey_NothingIsAnActivation_AndNothingIsWritten()
    {
        string root = NewRoot();
        PrivonLocalStore.OpenOrCreate(root);
        using var held = new FileStream(Path.Combine(root, "master.key"), FileMode.Open, FileAccess.Read, FileShare.None);
        var store = PrivonLocalStore.OpenOrCreate(root);

        Assert.True(store.IsMasterKeyUnavailable);
        Assert.Equal(CountryChoiceState.Unauthenticated, store.LoadCountryActivation(Us).State);
        Assert.Throws<InvalidOperationException>(() => store.SaveCountryActivation(Us, CountryChoiceOrigin.SettingsUserAction));
        Assert.False(File.Exists(ActivationFile(root)));
    }

    [Fact]
    public void ADamagedActivation_IsReplacedByTheNextExplicitActivation_WithoutManualRepair()
    {
        string root = NewRoot();
        var store = PrivonLocalStore.OpenOrCreate(root);
        File.WriteAllBytes(ActivationFile(root), [1, 2, 3]);
        Assert.Equal(CountryChoiceState.Corrupt, store.LoadCountryActivation(Us).State);

        store.SaveCountryActivation(Us, CountryChoiceOrigin.SettingsUserAction);

        Assert.Equal(CountryChoiceState.On, store.LoadCountryActivation(Us).State);
    }

    [Fact]
    public void AFailedWrite_LeavesTheExistingActivationAsItWas()
    {
        string root = NewRoot();
        var store = PrivonLocalStore.OpenOrCreate(root);
        store.SaveCountryActivation(Us, CountryChoiceOrigin.SettingsUserAction);
        byte[] before = File.ReadAllBytes(ActivationFile(root));

        using (new FileStream(ActivationFile(root), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            // replacing a file another handle holds fails with an IO or access error (Windows reports it as either)
            var failure = Record.Exception(() => store.SaveCountryActivation("Korea", CountryChoiceOrigin.SettingsUserAction));
            Assert.True(failure is IOException or UnauthorizedAccessException, failure?.GetType().Name ?? "no exception");
        }

        Assert.Equal(before, File.ReadAllBytes(ActivationFile(root)));
        Assert.Equal(CountryChoiceState.On, store.LoadCountryActivation(Us).State);
    }
}
