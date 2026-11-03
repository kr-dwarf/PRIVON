using Privon.Storage;

namespace Privon.App.Tests;

// The T-1 boundary as it stands in the shipped source. The store can now HOLD an authenticated per-country choice, but nothing in the product records
// one: the Settings surface has no country event. These tests pin that fact so a future writer is a deliberate, reviewed change (it would have to update
// them) and never an accident -- and so the user's existing settings and the shipped app are provably untouched by the storage foundation.
public sealed class CountryChoiceBoundaryTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "Privon.Storage", "CountryChoice.cs"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static IEnumerable<string> ProductSources() =>
        Directory.EnumerateFiles(Path.Combine(Root(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));

    [Fact]
    public void ExactlyOneProductPathRecordsACountryChoice_TheSettingsCoordinatorThroughTheCountryChoiceService()
    {
        // 0.4.0 T-1 UI path: the store's writer is called by CountryChoiceService only, and that service's setter is called by SettingsCoordinator only.
        var writerCallers = ProductSources()
            .Where(f => File.ReadAllText(f).Contains("SaveCountryChoice(", StringComparison.Ordinal))
            .Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        var setterCallers = ProductSources()
            .Where(f => File.ReadAllText(f).Contains("SetUnitedStates(", StringComparison.Ordinal))
            .Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(["CountryChoiceService.cs", "PrivonLocalStore.cs"], writerCallers);                 // the service, and the store's own definition
        Assert.Equal(["CountryChoiceService.cs", "SettingsCoordinator.cs"], setterCallers);              // its definition, and the one coordinator handler
        // the user-action origin is named only by the store's two files and that one service, never by a producer
        Assert.DoesNotContain(ProductSources(), f => File.ReadAllText(f).Contains("CountryChoiceOrigin.SettingsUserAction", StringComparison.Ordinal)
            && Path.GetFileName(f) is not ("CountryChoice.cs" or "PrivonLocalStore.cs" or "CountryChoiceService.cs"));
    }

    [Fact]
    public void TheSettingsSurface_HasExactlyOneCountryEvent_AndNoOtherCountryEventAnywhere()
    {
        var surfaceEvents = typeof(ISettingsSurface).GetEvents().Select(e => e.Name).ToList();
        Assert.Equal(["UnitedStatesToggleRequested"], surfaceEvents.Where(n => n.Contains("Country") || n.Contains("UnitedStates")).ToList());

        // across the whole app assembly, the only country-named events are the surface's request and the coordinator's after-it-is-stored notification
        var allCountryEvents = typeof(PrivonAppComposition).Assembly.GetTypes().SelectMany(t => t.GetEvents(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
            .Select(e => e.Name).Where(n => n.Contains("Country") || n.Contains("UnitedStates")).Distinct().Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["CountryChoiceChanged", "UnitedStatesToggleRequested"], allCountryEvents);

        foreach (string file in new[] { "SettingsViewState.cs", "ProtectionCategorySettingsService.cs" })
        {
            string text = File.ReadAllText(Path.Combine(Root(), "src", "Privon.App", file));
            Assert.DoesNotContain("SaveCountryChoice", text);
        }
    }

    [Fact]
    public void TheStoreExposesExactlyTheCountryChoiceAndActivationOperations_AndTheOnlyAcceptedOriginIsASettingsUserAction()
    {
        var names = typeof(PrivonLocalStore).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .Select(m => m.Name).Where(n => n.Contains("Country", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();

        // the two country-choice operations of T-1, plus the load / save of the explicit ACTIVATION record (P1) -- there is no way to deactivate or to write one without an origin
        Assert.Equal(["LoadCountryActivation", "LoadCountryChoice", "SaveCountryActivation", "SaveCountryChoice"], names);
        Assert.Equal(["SettingsUserAction", "Unknown"], Enum.GetNames<CountryChoiceOrigin>().Order(StringComparer.Ordinal));
        Assert.Equal(0, (int)CountryChoiceOrigin.Unknown);                    // the zero value is "not a user action" and is refused on write
    }

    [Fact]
    public void TheShippedStartup_OffersTheChoiceOnlyThroughTheGatedFactory_AndThePublicConstructorStillPassesNoCapability()
    {
        string app = File.ReadAllText(Path.Combine(Root(), "src", "Privon.App", "App.xaml.cs"));
        string composition = File.ReadAllText(Path.Combine(Root(), "src", "Privon.App", "PrivonAppComposition.cs"));
        string publicCtor = composition[composition.IndexOf("public PrivonAppComposition()", StringComparison.Ordinal)..];
        publicCtor = publicCtor[..publicCtor.IndexOf("{", StringComparison.Ordinal)];

        foreach (string token in new[] { "CountryChoice", "LoadCountryChoice", "StoreBackedCountrySettingSource", "country-settings", "offerUnitedStatesChoice", "UnitedStates", "UsGatedOptInOwner" })
        {
            Assert.DoesNotContain(token, app);
            Assert.DoesNotContain(token, publicCtor);
        }

        // the shipped startup asks for the gated factory by name; the plain constructor remains the composition with no U.S. control at all
        Assert.Contains("PrivonAppComposition.CreateProduction()", app);
        Assert.DoesNotContain("new PrivonAppComposition()", app);
        Assert.Contains("bool offerUnitedStatesChoice = false", composition);               // the opt-in still defaults to off for every other construction

        // the production bridge hands the composition's capability (null when not offered) to the coordinator, and to nothing else
        string bridge = File.ReadAllText(Path.Combine(Root(), "src", "Privon.App", "PrivonAppUiBridge.cs"));
        Assert.Contains("composition.CountryChoices", bridge);
        Assert.DoesNotContain("new CountryChoiceService", bridge);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(composition, "new CountryChoiceService").Count);
    }
}
