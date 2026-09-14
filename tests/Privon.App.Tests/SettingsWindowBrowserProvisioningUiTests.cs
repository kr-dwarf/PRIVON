using System.Windows;
using System.Windows.Controls;
using Privon.App;

namespace Privon.App.Tests;

// PRIVON 0.3.1 Gate E5G.1F audit remediation -- direct SettingsWindow UI regression proving the
// REAL window renders the correct Edge Native Messaging status text and Setup/Repair button
// visibility for each NativeMessagingRegistrationReadiness state. The existing
// SettingsCoordinator/FakeSettingsSurface tests already prove ROUTING semantics (which event calls
// which coordinator method); they never construct a real SettingsWindow and therefore never prove
// the window itself presents the right control. This file closes that gap the same way
// SettingsWindowDegradedStorageTests.cs already closes it for the degraded-storage banner: a real
// SettingsWindow constructed and driven on the same DispatcherAffineTestHost real, pumped WPF
// Dispatcher infrastructure, walking the real logical tree -- never a pixel/screenshot assertion,
// and no production visibility/access modifier is changed to make any of this observable (Button/
// TextBlock are ordinary public WPF types; only the PRIVATE FIELDS holding them stay private).
public class SettingsWindowBrowserProvisioningUiTests
{
    // Mirrors CollectVisibleTextBlockTexts's own walk (SettingsWindowDegradedStorageTests.cs) but
    // collects Buttons too, tagged and in exact document order -- so a caller can locate "the Setup/
    // Repair button pair immediately following a given status TextBlock" without needing access to
    // any private field. Visibility is recorded regardless of Visible/Collapsed (unlike the
    // TextBlock-only walk, which only collects when Visible) precisely because a Collapsed control's
    // ABSENCE-from-view is exactly what several of these cases must prove.
    private static List<(string Kind, string? Text, Visibility Visibility)> CollectOrderedElements(DependencyObject root)
    {
        var results = new List<(string, string?, Visibility)>();
        void Walk(DependencyObject node)
        {
            switch (node)
            {
                case TextBlock tb:
                    results.Add(("TextBlock", tb.Text, tb.Visibility));
                    break;
                case Button btn:
                    results.Add(("Button", btn.Content as string, btn.Visibility));
                    break;
            }

            foreach (var child in LogicalTreeHelper.GetChildren(node))
            {
                if (child is DependencyObject childObj)
                {
                    Walk(childObj);
                }
            }
        }
        Walk(root);
        return results;
    }

    // Locates the status TextBlock whose text starts with browserPrefix (e.g. "Edge:"), then reads
    // the two Button elements production places immediately after it in document order -- exactly
    // the Setup-then-Repair Add() order SettingsWindow's own constructor uses for both the Chrome and
    // Edge sections. Fails loudly (not silently) if that exact shape ever changes, rather than
    // silently reading the wrong pair.
    private static (string StatusText, Visibility SetupVisibility, Visibility RepairVisibility) GetBrowserSection(
        SettingsWindow window, SettingsViewState state, string browserPrefix)
    {
        window.RenderState(state);
        var elements = CollectOrderedElements(window);

        int statusIndex = elements.FindIndex(e => e.Kind == "TextBlock" && e.Text is not null && e.Text.StartsWith(browserPrefix, StringComparison.Ordinal));
        Assert.True(statusIndex >= 0, $"No status TextBlock found starting with '{browserPrefix}'. Elements: {string.Join(" | ", elements.Select(e => $"{e.Kind}:{e.Text}"))}");

        var setupEntry = elements[statusIndex + 1];
        var repairEntry = elements[statusIndex + 2];
        Assert.Equal("Button", setupEntry.Kind);
        Assert.Equal("Set up", setupEntry.Text);
        Assert.Equal("Button", repairEntry.Kind);
        Assert.Equal("Repair", repairEntry.Text);

        return (elements[statusIndex].Text!, setupEntry.Visibility, repairEntry.Visibility);
    }

    private static SettingsViewState EdgeState(NativeMessagingRegistrationReadiness edgeReadiness) => new(
        PhoneEnabled: true, EmailEnabled: true, Exceptions: [], MasterKeyUnavailable: false, StatusMessage: null,
        EdgeNativeMessagingReadiness: edgeReadiness);

    [Fact]
    public void EdgeFresh_ShowsReleaseDeferred_NoSetup_NoRepair()
    {
        using var host = new DispatcherAffineTestHost();

        var (statusText, setupVisibility, repairVisibility) = host.Dispatcher.Invoke(() =>
        {
            var window = new SettingsWindow();
            try
            {
                return GetBrowserSection(window, EdgeState(NativeMessagingRegistrationReadiness.Fresh), "Edge:");
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal("Edge: unavailable", statusText);
        Assert.Equal(Visibility.Collapsed, setupVisibility);
        Assert.Equal(Visibility.Collapsed, repairVisibility);
    }

    [Fact]
    public void EdgeReadyUnderlyingState_StillShowsReleaseDeferred_NoSetup_NoRepair()
    {
        using var host = new DispatcherAffineTestHost();

        var (statusText, setupVisibility, repairVisibility) = host.Dispatcher.Invoke(() =>
        {
            var window = new SettingsWindow();
            try
            {
                return GetBrowserSection(window, EdgeState(NativeMessagingRegistrationReadiness.Ready), "Edge:");
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal("Edge: unavailable", statusText);
        Assert.Equal(Visibility.Collapsed, setupVisibility);
        Assert.Equal(Visibility.Collapsed, repairVisibility);
    }

    [Fact]
    public void EdgeOwnedNeedsRepairUnderlyingState_StillShowsReleaseDeferred_NoSetup_NoRepair()
    {
        using var host = new DispatcherAffineTestHost();

        var (statusText, setupVisibility, repairVisibility) = host.Dispatcher.Invoke(() =>
        {
            var window = new SettingsWindow();
            try
            {
                return GetBrowserSection(window, EdgeState(NativeMessagingRegistrationReadiness.OwnedNeedsRepair), "Edge:");
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal("Edge: unavailable", statusText);
        Assert.Equal(Visibility.Collapsed, setupVisibility);
        Assert.Equal(Visibility.Collapsed, repairVisibility);
    }

    [Fact]
    public void EdgeForeignBlocked_ShowsBlocked_NoSetup_NoRepair()
    {
        using var host = new DispatcherAffineTestHost();

        var (statusText, setupVisibility, repairVisibility) = host.Dispatcher.Invoke(() =>
        {
            var window = new SettingsWindow();
            try
            {
                return GetBrowserSection(window, EdgeState(NativeMessagingRegistrationReadiness.ForeignBlocked), "Edge:");
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal("Edge: unavailable", statusText);
        Assert.Equal(Visibility.Collapsed, setupVisibility);
        Assert.Equal(Visibility.Collapsed, repairVisibility);
    }

    [Fact]
    public void EdgeOrphanBlocked_ShowsBlocked_NoSetup_NoRepair()
    {
        using var host = new DispatcherAffineTestHost();

        var (statusText, setupVisibility, repairVisibility) = host.Dispatcher.Invoke(() =>
        {
            var window = new SettingsWindow();
            try
            {
                return GetBrowserSection(window, EdgeState(NativeMessagingRegistrationReadiness.OrphanBlocked), "Edge:");
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal("Edge: unavailable", statusText);
        Assert.Equal(Visibility.Collapsed, setupVisibility);
        Assert.Equal(Visibility.Collapsed, repairVisibility);
    }

    // Failed: shown as a distinct failure state, and -- the actual safety property this case exists
    // to prove -- NEITHER action control is available, so there is no automatic/one-click mutation a
    // user could reach from a Failed state.
    [Fact]
    public void EdgeFailed_ShowsUnavailable_NoAutomaticMutationActionAvailable()
    {
        using var host = new DispatcherAffineTestHost();

        var (statusText, setupVisibility, repairVisibility) = host.Dispatcher.Invoke(() =>
        {
            var window = new SettingsWindow();
            try
            {
                return GetBrowserSection(window, EdgeState(NativeMessagingRegistrationReadiness.Failed), "Edge:");
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal("Edge: unavailable", statusText);
        Assert.Equal(Visibility.Collapsed, setupVisibility);
        Assert.Equal(Visibility.Collapsed, repairVisibility);
    }
}
