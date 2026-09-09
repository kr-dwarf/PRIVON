using System.Windows;
using System.Windows.Controls;

namespace Privon.App.Tests;

// PRIVON 0.3.2 Gate 032-C2 -- FRIENDLY CONNECT CHROME + PROMPTED RECONNECT.
//
// This file covers exactly the two surfaces the main PrivonAppUiBridgeTests/SettingsCoordinatorTests
// additions do NOT already cover: (1) the real SettingsWindow's own Chrome-section ordinary-user
// wording (Connect Chrome / Reconnect Chrome / "not connected" / "connected" / "connection needs
// updating"), proven the same way SettingsWindowBrowserProvisioningUiTests.cs already proves Edge's
// -- a real SettingsWindow constructed and driven on a real, pumped WPF Dispatcher, never a
// pixel/screenshot assertion; and (2) a direct proof, against the real frozen (unmodified)
// WebPipeEndpoint production type, that two different canonical executable paths produce two
// different endpoint names -- the exact mechanism Gate 032-C1 identified as why a stale Native
// Messaging manifest path can never reach a relocated tray instance. Edge's own existing "Set
// up"/"Repair" wording is asserted UNCHANGED in SettingsWindowBrowserProvisioningUiTests.cs and is
// never touched by this gate.
public class Gate032C2_ConnectChromePromptedReconnectRedTests
{
    // ==================================================================
    // A/B/C -- SettingsWindow Chrome-section ordinary-user wording.
    // Mirrors SettingsWindowBrowserProvisioningUiTests.CollectOrderedElements/GetBrowserSection
    // exactly, but intentionally NOT shared with that file: Chrome's button text is now DIFFERENT
    // from Edge's ("Connect Chrome"/"Reconnect Chrome" vs. Edge's unchanged "Set up"/"Repair"), so a
    // shared helper hardcoding either string would be wrong for the other browser.
    // ==================================================================

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

    private static (string StatusText, Visibility ConnectVisibility, Visibility ReconnectVisibility) GetChromeSection(
        SettingsWindow window, SettingsViewState state)
    {
        window.RenderState(state);
        var elements = CollectOrderedElements(window);

        int statusIndex = elements.FindIndex(e => e.Kind == "TextBlock" && e.Text is not null && e.Text.StartsWith("Chrome:", StringComparison.Ordinal));
        Assert.True(statusIndex >= 0, $"No status TextBlock found starting with 'Chrome:'. Elements: {string.Join(" | ", elements.Select(e => $"{e.Kind}:{e.Text}"))}");

        var connectEntry = elements[statusIndex + 1];
        var reconnectEntry = elements[statusIndex + 2];
        Assert.Equal("Button", connectEntry.Kind);
        Assert.Equal("Button", reconnectEntry.Kind);

        return (elements[statusIndex].Text!, connectEntry.Visibility, reconnectEntry.Visibility);
    }

    private static SettingsViewState ChromeState(NativeMessagingRegistrationReadiness chromeReadiness) => new(
        PhoneEnabled: true, EmailEnabled: true, Exceptions: [], MasterKeyUnavailable: false, StatusMessage: null,
        ChromeNativeMessagingReadiness: chromeReadiness);

    [Fact]
    public void ChromeFresh_ShowsNotConnected_ConnectChromeButtonVisible_NoReconnectButton()
    {
        using var host = new DispatcherAffineTestHost();

        var (statusText, connectVisibility, reconnectVisibility) = host.Dispatcher.Invoke(() =>
        {
            var window = new SettingsWindow();
            try
            {
                return GetChromeSection(window, ChromeState(NativeMessagingRegistrationReadiness.Fresh));
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal("Chrome: not connected", statusText);
        Assert.Equal(Visibility.Visible, connectVisibility);
        Assert.Equal(Visibility.Collapsed, reconnectVisibility);
    }

    [Fact]
    public void ChromeFresh_ConnectButtonText_IsConnectChrome_NeverSetUp()
    {
        using var host = new DispatcherAffineTestHost();

        var connectText = host.Dispatcher.Invoke(() =>
        {
            var window = new SettingsWindow();
            try
            {
                window.RenderState(ChromeState(NativeMessagingRegistrationReadiness.Fresh));
                var elements = CollectOrderedElements(window);
                int statusIndex = elements.FindIndex(e => e.Kind == "TextBlock" && e.Text == "Chrome: not connected");
                return elements[statusIndex + 1].Text;
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal("Connect Chrome", connectText);
    }

    [Fact]
    public void ChromeReady_ShowsConnected_NeitherButtonVisible()
    {
        using var host = new DispatcherAffineTestHost();

        var (statusText, connectVisibility, reconnectVisibility) = host.Dispatcher.Invoke(() =>
        {
            var window = new SettingsWindow();
            try
            {
                return GetChromeSection(window, ChromeState(NativeMessagingRegistrationReadiness.Ready));
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal("Chrome: connected", statusText);
        Assert.Equal(Visibility.Collapsed, connectVisibility);
        Assert.Equal(Visibility.Collapsed, reconnectVisibility);
    }

    [Fact]
    public void ChromeOwnedNeedsRepair_ShowsConnectionNeedsUpdating_ReconnectButtonVisible_NoConnectButton()
    {
        using var host = new DispatcherAffineTestHost();

        var (statusText, connectVisibility, reconnectVisibility) = host.Dispatcher.Invoke(() =>
        {
            var window = new SettingsWindow();
            try
            {
                return GetChromeSection(window, ChromeState(NativeMessagingRegistrationReadiness.OwnedNeedsRepair));
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal("Chrome: connection needs updating", statusText);
        Assert.Equal(Visibility.Collapsed, connectVisibility);
        Assert.Equal(Visibility.Visible, reconnectVisibility);
    }

    [Fact]
    public void ChromeOwnedNeedsRepair_ReconnectButtonText_IsReconnectChrome_NeverRepair()
    {
        using var host = new DispatcherAffineTestHost();

        var reconnectText = host.Dispatcher.Invoke(() =>
        {
            var window = new SettingsWindow();
            try
            {
                window.RenderState(ChromeState(NativeMessagingRegistrationReadiness.OwnedNeedsRepair));
                var elements = CollectOrderedElements(window);
                int statusIndex = elements.FindIndex(e => e.Kind == "TextBlock" && e.Text == "Chrome: connection needs updating");
                return elements[statusIndex + 2].Text;
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal("Reconnect Chrome", reconnectText);
    }

    // Existing ForeignBlocked/OrphanBlocked/Failed diagnostic wording may remain unchanged (Gate
    // 032-C2 section 7) -- these three cases prove exactly that: no button ever appears for a
    // blocked/unavailable state, and the section 8-mandated non-technical vocabulary is never
    // violated by whatever text IS shown.
    //
    // NOTE: NativeMessagingRegistrationReadiness is `internal` -- an xUnit [Theory] method must be
    // public, and a public method may never expose a less-accessible parameter type (CS0051), so
    // each case is its own [Fact] (matching this project's established convention -- see
    // SettingsWindowBrowserProvisioningUiTests.cs, which does the same for the identical reason)
    // rather than a [Theory]/[InlineData] over the enum.
    private static void AssertNeitherActionButtonVisible(NativeMessagingRegistrationReadiness readiness)
    {
        using var host = new DispatcherAffineTestHost();

        var (_, connectVisibility, reconnectVisibility) = host.Dispatcher.Invoke(() =>
        {
            var window = new SettingsWindow();
            try
            {
                return GetChromeSection(window, ChromeState(readiness));
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal(Visibility.Collapsed, connectVisibility);
        Assert.Equal(Visibility.Collapsed, reconnectVisibility);
    }

    [Fact]
    public void ChromeForeignBlocked_NeitherActionButtonVisible() =>
        AssertNeitherActionButtonVisible(NativeMessagingRegistrationReadiness.ForeignBlocked);

    [Fact]
    public void ChromeOrphanBlocked_NeitherActionButtonVisible() =>
        AssertNeitherActionButtonVisible(NativeMessagingRegistrationReadiness.OrphanBlocked);

    [Fact]
    public void ChromeFailed_NeitherActionButtonVisible() =>
        AssertNeitherActionButtonVisible(NativeMessagingRegistrationReadiness.Failed);

    // ==================================================================
    // Section 8 -- forbidden ordinary-user vocabulary must never appear in what SettingsWindow
    // actually renders for the Chrome section, across every reachable readiness.
    // ==================================================================

    private static readonly string[] ForbiddenTechnicalTerms =
        ["Native Messaging", "registry", "manifest", "host path", "레지스트리", "매니페스트"];

    private static void AssertChromeSectionNeverRendersTechnicalVocabulary(NativeMessagingRegistrationReadiness readiness)
    {
        using var host = new DispatcherAffineTestHost();

        var texts = host.Dispatcher.Invoke(() =>
        {
            var window = new SettingsWindow();
            try
            {
                window.RenderState(ChromeState(readiness));
                return CollectOrderedElements(window).Select(e => e.Text).Where(t => t is not null).ToList();
            }
            finally
            {
                window.Close();
            }
        });

        foreach (var term in ForbiddenTechnicalTerms)
        {
            Assert.DoesNotContain(texts, t => t!.Contains(term, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void ChromeSection_Fresh_NeverRendersTechnicalVocabulary() =>
        AssertChromeSectionNeverRendersTechnicalVocabulary(NativeMessagingRegistrationReadiness.Fresh);

    [Fact]
    public void ChromeSection_Ready_NeverRendersTechnicalVocabulary() =>
        AssertChromeSectionNeverRendersTechnicalVocabulary(NativeMessagingRegistrationReadiness.Ready);

    [Fact]
    public void ChromeSection_OwnedNeedsRepair_NeverRendersTechnicalVocabulary() =>
        AssertChromeSectionNeverRendersTechnicalVocabulary(NativeMessagingRegistrationReadiness.OwnedNeedsRepair);

    [Fact]
    public void ChromeSection_ForeignBlocked_NeverRendersTechnicalVocabulary() =>
        AssertChromeSectionNeverRendersTechnicalVocabulary(NativeMessagingRegistrationReadiness.ForeignBlocked);

    [Fact]
    public void ChromeSection_OrphanBlocked_NeverRendersTechnicalVocabulary() =>
        AssertChromeSectionNeverRendersTechnicalVocabulary(NativeMessagingRegistrationReadiness.OrphanBlocked);

    [Fact]
    public void ChromeSection_Failed_NeverRendersTechnicalVocabulary() =>
        AssertChromeSectionNeverRendersTechnicalVocabulary(NativeMessagingRegistrationReadiness.Failed);

    // ==================================================================
    // Section 9/6 -- production must never claim it knows the Store extension is installed. The
    // "not connected"/"connected"/"connection needs updating" copy this gate introduces describes
    // the desktop<->Chrome NATIVE MESSAGING relationship only, never "extension detected".
    // ==================================================================

    [Fact]
    public void ChromeSection_NeverClaimsExtensionDetection()
    {
        using var host = new DispatcherAffineTestHost();

        var texts = host.Dispatcher.Invoke(() =>
        {
            var window = new SettingsWindow();
            try
            {
                window.RenderState(ChromeState(NativeMessagingRegistrationReadiness.Ready));
                return CollectOrderedElements(window).Select(e => e.Text).Where(t => t is not null).ToList();
            }
            finally
            {
                window.Close();
            }
        });

        Assert.DoesNotContain(texts, t => t!.Contains("extension detected", StringComparison.OrdinalIgnoreCase));
    }

    // ==================================================================
    // Item I (partial) -- WebPipeEndpoint itself (FROZEN, unmodified by this gate) is exactly why a
    // stale manifest path can never reach a relocated tray instance: two different canonical
    // executable paths, same SID, must produce two DIFFERENT endpoint names. Test-only use of the
    // real production type -- WebPipeEndpoint.cs source is not touched anywhere in this gate.
    // ==================================================================

    [Fact]
    public void WebPipeEndpoint_DifferentCanonicalPaths_ProduceDistinctEndpoints_SameSid()
    {
        const string sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

        string oldEndpoint = WebPipeEndpoint.Compute(sid, @"C:\PRIVON-OLD\PRIVON.exe");
        string newEndpoint = WebPipeEndpoint.Compute(sid, @"C:\PRIVON-NEW\PRIVON.exe");

        Assert.NotEqual(oldEndpoint, newEndpoint);
    }

    [Fact]
    public void WebPipeEndpoint_SameCanonicalPath_SameSid_IsDeterministic()
    {
        const string sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

        string first = WebPipeEndpoint.Compute(sid, @"C:\PRIVON\PRIVON.exe");
        string second = WebPipeEndpoint.Compute(sid, @"C:\PRIVON\PRIVON.exe");

        Assert.Equal(first, second);
    }
}
