namespace Privon.App.Tests;

// PRIVON v0.2.1 Gate 3C -- structural source scans proving the Settings UI's own allowed-controls
// and privacy boundaries (UI-002/UI-014/UI-015/UI-020's title/diagnostics-surface leg). Real WPF
// Window/WinForms NotifyIcon construction needs a live STA/Dispatcher-pumped thread -- real
// rendering remains manual release QA (same established precedent as
// PrivonAppUiBridgeTests.NewUiSourceFiles_NeverContainForbiddenClaimsOrActions) -- these tests
// instead scan the actual source files so a future edit that quietly adds a forbidden control/type
// reference fails loudly here.
public class SettingsUiStructuralTests
{
    // ==================================================================
    // UI-002 / UI-014 / UI-015 -- only Phone/Email are exposed as protection controls; Name/
    // Address/Company/IP/MAC/GPS never appear as a selectable/interactive control.
    // ==================================================================
    private static readonly string[] ForbiddenCategoryOrTypeReferences =
    [
        "NameEnabled", "AddressEnabled", "CompanyEnabled",
        "PiiType.IpAddress", "PiiType.MacAddress", "PiiType.GpsCoordinate",
    ];

    [Fact]
    public void SettingsWindowSource_NeverReferencesUnsupportedCategoryOrTypeControls()
    {
        var source = StripDocComments(File.ReadAllText(FindAppSourceFile("SettingsWindow.cs")));

        foreach (var term in ForbiddenCategoryOrTypeReferences)
        {
            Assert.DoesNotContain(term, source);
        }
    }

    [Fact]
    public void SettingsCoordinatorSource_NeverReferencesUnsupportedCategoryOrTypeControls()
    {
        var source = StripDocComments(File.ReadAllText(FindAppSourceFile("SettingsCoordinator.cs")));

        foreach (var term in ForbiddenCategoryOrTypeReferences)
        {
            Assert.DoesNotContain(term, source);
        }
    }

    // ---- UI-013 companion: Level3/high-risk exception types never appear as selectable options
    // either (the ONLY two RadioButton-backed selectable types are Phone/Email -- see
    // SettingsWindow's own _typePhone/_typeEmail fields). ----
    [Theory]
    [InlineData("PiiType.ResidentRegistrationNumber")]
    [InlineData("PiiType.CardNumber")]
    [InlineData("PiiType.BankAccountNumber")]
    [InlineData("PiiType.Secret")]
    public void SettingsWindowSource_NeverReferencesLevel3ExceptionTypes(string term)
    {
        var source = StripDocComments(File.ReadAllText(FindAppSourceFile("SettingsWindow.cs")));
        Assert.DoesNotContain(term, source);
    }

    // ==================================================================
    // UI-020 -- privacy: the window Title is a fixed literal, never built from any runtime/PII
    // value.
    // ==================================================================
    [Fact]
    public void SettingsWindowSource_TitleIsAFixedLiteral_NeverInterpolated()
    {
        var source = File.ReadAllText(FindAppSourceFile("SettingsWindow.cs"));
        Assert.Contains("Title = \"PRIVON Settings\";", source);
        Assert.DoesNotContain("Title = $", source);
    }

    // ==================================================================
    // No clipboard/target/retry/generation logic belongs in the Settings UI.
    // ==================================================================
    private static readonly string[] ForbiddenClipboardTargetTerms =
    [
        "ClipboardChangeMonitor", "ForegroundTargetCapture", "TargetGate", "ClipboardRetryDelay",
        "ClipboardOperationGate", "ClipboardDecisionScopeLifecycle",
    ];

    [Theory]
    [InlineData("SettingsWindow.cs")]
    [InlineData("SettingsCoordinator.cs")]
    [InlineData("ProtectionCategorySettingsService.cs")]
    public void SettingsSourceFiles_NeverReferenceClipboardOrTargetTypes(string fileName)
    {
        var source = File.ReadAllText(FindAppSourceFile(fileName));

        foreach (var term in ForbiddenClipboardTargetTerms)
        {
            Assert.DoesNotContain(term, source);
        }
    }

    // ==================================================================
    // Tray: exactly one new menu item ("Settings..."), no new tray icon/process.
    // ==================================================================
    [Fact]
    public void TrayIconSurfaceSource_AddsExactlyOneSettingsMenuItem_NoSecondNotifyIcon()
    {
        var source = File.ReadAllText(FindAppSourceFile("WinFormsTrayIconSurface.cs"));
        Assert.Contains("\"Settings...\"", source);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(source, "new NotifyIcon"));
    }

    private static string FindAppSourceDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Privon.App")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
            throw new InvalidOperationException("Could not locate src/Privon.App from the test output directory.");

        return Path.Combine(dir.FullName, "src", "Privon.App");
    }

    private static string FindAppSourceFile(string fileName) =>
        Path.Combine(FindAppSourceDirectory(), fileName);

    private static string StripDocComments(string source) =>
        string.Join('\n', source.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
}
