using System.Windows;
using System.Windows.Controls;
using Privon.App;

namespace Privon.App.Tests;

// PRIVON v0.2.1 Gate 3C DEGRADED_STORAGE_STATUS_CORRECTION -- UI-027: the real SettingsWindow
// rendering contract. MasterKeyUnavailable=true and a non-empty StatusMessage must BOTH be
// represented simultaneously -- the degraded-storage explanation must never be erased by a
// mutation-failure message (see SettingsCoordinatorTests.cs's own Ui016B/Ui016C for the
// coordinator-side half of this same contract). Uses the same real, pumped-Dispatcher
// DispatcherAffineTestHost infrastructure as SettingsWindowActivationTests.cs -- a structural walk
// of the real visual tree, never a pixel/screenshot assertion.
public class SettingsWindowDegradedStorageTests
{
    // ---- UI-027 -- both facts visible simultaneously. ----
    [Fact]
    public void RenderState_MasterKeyUnavailableAndStatusMessage_BothVisibleSimultaneously()
    {
        using var host = new DispatcherAffineTestHost();

        var (degradedVisible, statusVisible) = host.Dispatcher.Invoke(() =>
        {
            var window = new SettingsWindow();
            try
            {
                window.RenderState(new SettingsViewState(
                    PhoneEnabled: true, EmailEnabled: true, Exceptions: [],
                    MasterKeyUnavailable: true, StatusMessage: SettingsCoordinator.GenericFailureMessage));

                var texts = CollectVisibleTextBlockTexts(window);
                bool degraded = texts.Any(t => t.Contains(SettingsCoordinator.MasterKeyUnavailableBanner, StringComparison.Ordinal));
                bool status = texts.Any(t => t.Contains(SettingsCoordinator.GenericFailureMessage, StringComparison.Ordinal));
                return (degraded, status);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.True(degradedVisible, "The degraded-storage explanation must remain visible even when a mutation-failure StatusMessage is also present.");
        Assert.True(statusVisible, "The mutation-failure StatusMessage must also be visible alongside the degraded-storage explanation.");
    }

    // ---- companion -- when MasterKeyUnavailable is false, the degraded banner must never appear,
    // even with a StatusMessage present (normal-mode failure UX, UI-025A's window-level half). ----
    [Fact]
    public void RenderState_NormalModeWithStatusMessage_NeverShowsDegradedBanner()
    {
        using var host = new DispatcherAffineTestHost();

        var (degradedVisible, statusVisible) = host.Dispatcher.Invoke(() =>
        {
            var window = new SettingsWindow();
            try
            {
                window.RenderState(new SettingsViewState(
                    PhoneEnabled: true, EmailEnabled: true, Exceptions: [],
                    MasterKeyUnavailable: false, StatusMessage: SettingsCoordinator.GenericFailureMessage));

                var texts = CollectVisibleTextBlockTexts(window);
                bool degraded = texts.Any(t => t.Contains(SettingsCoordinator.MasterKeyUnavailableBanner, StringComparison.Ordinal));
                bool status = texts.Any(t => t.Contains(SettingsCoordinator.GenericFailureMessage, StringComparison.Ordinal));
                return (degraded, status);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.False(degradedVisible);
        Assert.True(statusVisible);
    }

    // ---- companion -- MasterKeyUnavailable true, no StatusMessage -> only the degraded banner is
    // visible (no stray/empty status region shown). ----
    [Fact]
    public void RenderState_DegradedOnly_NoStatusMessage_OnlyBannerVisible()
    {
        using var host = new DispatcherAffineTestHost();

        var (degradedVisible, statusVisible) = host.Dispatcher.Invoke(() =>
        {
            var window = new SettingsWindow();
            try
            {
                window.RenderState(new SettingsViewState(
                    PhoneEnabled: true, EmailEnabled: true, Exceptions: [],
                    MasterKeyUnavailable: true, StatusMessage: null));

                var texts = CollectVisibleTextBlockTexts(window);
                bool degraded = texts.Any(t => t.Contains(SettingsCoordinator.MasterKeyUnavailableBanner, StringComparison.Ordinal));
                bool status = texts.Any(t => t.Contains(SettingsCoordinator.GenericFailureMessage, StringComparison.Ordinal));
                return (degraded, status);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.True(degradedVisible);
        Assert.False(statusVisible);
    }

    // ---- privacy companion (UI-020 continuation): the degraded banner never contains master-key/
    // DPAPI/crypto/filesystem detail -- only the fixed, approved generic wording. ----
    [Fact]
    public void RenderState_DegradedBanner_NeverExposesInternalDetail()
    {
        using var host = new DispatcherAffineTestHost();

        var texts = host.Dispatcher.Invoke(() =>
        {
            var window = new SettingsWindow();
            try
            {
                window.RenderState(new SettingsViewState(
                    PhoneEnabled: true, EmailEnabled: true, Exceptions: [],
                    MasterKeyUnavailable: true, StatusMessage: null));
                return CollectVisibleTextBlockTexts(window);
            }
            finally
            {
                window.Close();
            }
        });

        var joined = string.Join('\n', texts);
        Assert.DoesNotContain("master.key", joined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DPAPI", joined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(":\\", joined); // no filesystem path
    }

    private static List<string> CollectVisibleTextBlockTexts(DependencyObject root)
    {
        var results = new List<string>();
        void Walk(DependencyObject node)
        {
            if (node is TextBlock { Visibility: Visibility.Visible } tb && !string.IsNullOrEmpty(tb.Text))
            {
                results.Add(tb.Text);
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
}
