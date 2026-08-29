using Privon.App;

namespace Privon.App.Tests;

// PRIVON v0.2.1 Gate 3C -- the ONE behavior that genuinely requires a real WPF window: proving
// SettingsWindow activates NORMALLY, the exact opposite of DecisionPromptWindow's own
// NON_ACTIVATING_PROMPT contract (see DecisionPromptWindowActivationTests.cs, the established
// precedent this file mirrors). No WS_EX_NOACTIVATE, no ShowActivated=false -- Settings needs real
// keyboard focus for exception-value entry. Uses the same real, pumped-Dispatcher
// DispatcherAffineTestHost infrastructure -- never a fragile pixel/layout assertion.
public class SettingsWindowActivationTests
{
    [Fact]
    public void SettingsWindow_ShowActivatedIsTrue_NormalWindowDefault()
    {
        using var host = new DispatcherAffineTestHost();

        bool showActivated = host.Dispatcher.Invoke(() =>
        {
            var window = new SettingsWindow();
            try
            {
                return window.ShowActivated;
            }
            finally
            {
                window.Close();
            }
        });

        Assert.True(showActivated);
    }

    // Structural companion to the real-window assertion above -- proves no NON_ACTIVATING_PROMPT-
    // style native interop was copied into this window (SettingsWindow.cs has no NativeMethods
    // class / WS_EX_NOACTIVATE reference of any kind, unlike DecisionPromptWindow.cs).
    [Fact]
    public void SettingsWindowSource_NeverReferencesNoActivateStyle()
    {
        var source = StripDocComments(File.ReadAllText(FindAppSourceFile("SettingsWindow.cs")));
        Assert.DoesNotContain("WS_EX_NOACTIVATE", source);
        Assert.DoesNotContain("ShowActivated = false", source);
    }

    private static string StripDocComments(string source) =>
        string.Join('\n', source.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    [Fact]
    public void SettingsWindow_ConstructsAndClosesOnARealDispatcherThread_WithoutThrowing()
    {
        using var host = new DispatcherAffineTestHost();

        var exception = Record.Exception(() => host.Dispatcher.Invoke(() =>
        {
            var window = new SettingsWindow();
            window.RenderState(new SettingsViewState(
                PhoneEnabled: true, EmailEnabled: true, Exceptions: [], MasterKeyUnavailable: false, StatusMessage: null));
            window.Show();
            window.Close();
        }));

        Assert.Null(exception);
    }

    private static string FindAppSourceFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Privon.App")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
            throw new InvalidOperationException("Could not locate src/Privon.App from the test output directory.");

        return Path.Combine(dir.FullName, "src", "Privon.App", fileName);
    }
}
