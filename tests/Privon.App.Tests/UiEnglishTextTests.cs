using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using Privon.App;
using Privon.Core;
using Privon.Detection;
using Privon.UsProductIntegration;

namespace Privon.App.Tests;

// SYNTHETIC DATA ONLY. The shipped app shows one display language -- English -- until a language choice exists. These tests pin (1) that no Korean text is left in
// any string the app source shows to a user, (2) the exact English wording of the texts the Controller named (Settings notes, tray lines, decision prompt, failure
// messages), and (3) that the PROTECTED-VALUE tokens (what is written to the clipboard) and the detection are not part of this language change.
public sealed class UiEnglishTextTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "Privon.App", "UsReadiness.cs"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static bool HasHangul(string s) => s.Any(c => c is >= '가' and <= '힣' or >= '㄰' and <= '㆏');

    // code only: whole-line comments and trailing // comments (a // inside a string literal is not a comment) are dropped
    private static IEnumerable<(string File, int Line, string Code)> CodeLines(string file)
    {
        int n = 0;
        foreach (string raw in File.ReadAllLines(file))
        {
            n++;
            string s = raw.TrimStart();
            if (s.StartsWith("//", StringComparison.Ordinal) || s.StartsWith('*') || s.StartsWith("/*", StringComparison.Ordinal)) continue;

            int quotes = 0;
            int cut = raw.Length;
            for (int i = 0; i + 1 < raw.Length; i++)
            {
                if (raw[i] == '"' && (i == 0 || raw[i - 1] != '\\')) quotes++;
                if (raw[i] == '/' && raw[i + 1] == '/' && quotes % 2 == 0) { cut = i; break; }
            }

            yield return (Path.GetFileName(file), n, raw[..cut]);
        }
    }

    [Fact]
    public void NoStringTheShippedAppCanShow_ContainsKorean()
    {
        string app = Path.Combine(Root(), "src", "Privon.App");
        var offenders = Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\"))
            .SelectMany(CodeLines)
            .Where(l => HasHangul(l.Code))
            .Select(l => $"{l.File}:{l.Line}")
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void TheTrayMenuAutoStartItem_AndTheRunningTooltip_AreEnglish()
    {
        string tray = File.ReadAllText(Path.Combine(Root(), "src", "Privon.App", "WinFormsTrayIconSurface.cs"));

        Assert.Contains("new ToolStripMenuItem(\"Launch at Windows Startup\")", tray);
        Assert.Contains("Text = \"PRIVON — Running\"", tray);
        Assert.Contains("\"Settings...\"", tray);                          // the English entries that were already English are untouched
        Assert.Contains("\"Exit\"", tray);
    }

    // ===== the U.S. line of the tray, one exact text per state ===================================================================

    private static UsReadinessReport Report(UsOptInGate gate, bool enabled = true, UsAuthorityProvenance provenance = UsAuthorityProvenance.Production, bool allReady = true) =>
        new(enabled, provenance,
            UsReadinessPresenterIds.Select(id => new UsDetectorReadiness(id, allReady ? UsDetectorAvailability.Ready : UsDetectorAvailability.NotProvided)).ToList(),
            "test", gate);

    private static readonly string[] UsReadinessPresenterIds =
    [
        "us.atin", "us.itin", "us.ssn", "us.mbi", "us.sevis", "us.passport", "us.a_number", "us.i94", "us.driver_state_id", "us.bank_account", "us.phone", "us.address", "us.name",
    ];

    [Fact]
    public void TheUsLine_StatesEachOptInStateInEnglish_AndKeepsReadyApartFromProtected()
    {
        Assert.Equal("U.S. Detection: 13/13 Ready (Verified Official Sources)", UsReadinessPresenter.Present(Report(UsOptInGate.OptedIn)).MenuText);
        Assert.Equal("U.S. Detection: 13/13 Ready (Verified Official Sources)", UsReadinessPresenter.Present(Report(UsOptInGate.NotGated)).MenuText);
        Assert.Equal("U.S. Detection: Off (Korean and Global detection remain active)", UsReadinessPresenter.Present(Report(UsOptInGate.OptedIn, enabled: false)).MenuText);
        Assert.Equal("U.S. Detection: Not Enabled (Korean and Global detection remain active)", UsReadinessPresenter.Present(Report(UsOptInGate.NotSelected, enabled: false)).MenuText);
        Assert.Equal("U.S. Detection: Off · Enablement Record Unverifiable", UsReadinessPresenter.Present(Report(UsOptInGate.Unverifiable, enabled: false)).MenuText);
        Assert.Equal("U.S. Detection: Unavailable (Official Source Verification Failed)", UsReadinessPresenter.Present(Report(UsOptInGate.Unavailable, enabled: false)).MenuText);
        Assert.Equal("U.S. Detection: Readiness Unknown", UsReadinessPresenter.Present(null).MenuText);
        Assert.Equal("U.S. Detection: 13/13 Ready (Test Data · Not Production-Verified)",
            UsReadinessPresenter.Present(Report(UsOptInGate.NotGated, provenance: UsAuthorityProvenance.Provided)).MenuText);

        foreach (var gate in new[] { UsOptInGate.OptedIn, UsOptInGate.NotSelected, UsOptInGate.Unverifiable, UsOptInGate.Unavailable })
        {
            var p = UsReadinessPresenter.Present(Report(gate, enabled: gate == UsOptInGate.OptedIn));
            Assert.False(HasHangul(p.MenuText + p.Detail));
            Assert.Contains("does not guarantee that sending is safe", p.Detail);                // "ready" is never protection and never send safety
            Assert.Contains("does not currently block sending", p.Detail);
        }
    }

    [Fact]
    public void ThePartialUsLine_NamesTheMissingDetectorsInEnglish()
    {
        var detectors = UsReadinessPresenterIds.Select(id =>
            new UsDetectorReadiness(id, id is "us.phone" or "us.address" ? UsDetectorAvailability.ValidationFailed : UsDetectorAvailability.Ready)).ToList();

        var p = UsReadinessPresenter.Present(new UsReadinessReport(true, UsAuthorityProvenance.Production, detectors, null, UsOptInGate.OptedIn));

        Assert.Equal("U.S. Detection: 11/13 Ready · Unavailable (data validation failed): Phone, Address", p.MenuText);
        Assert.False(HasHangul(p.MenuText + p.Detail));
    }

    // ===== the decision prompt, the failure messages and the Settings texts ======================================================

    private static T OnSta<T>(Func<T> body)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() => { try { result = body(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)));
        Assert.Null(failure);
        return result;
    }

    [Fact]
    public void TheDecisionPrompt_ShowsTheEnglishGuidanceAndTheProtectAllButton_WithTheSameControls()
    {
        var shown = OnSta(() =>
        {
            var window = new DecisionPromptWindow();
            var text = (TextBlock)typeof(DecisionPromptWindow).GetField("_messageText", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var button = (Button)typeof(DecisionPromptWindow).GetField("_protectAllButton", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var result = (text.Text, button.Content as string, window.Width, window.Height);
            window.Close();
            return result;
        });

        Assert.Equal("Personal information detected.\nSelect Protect All to protect the clipboard content.", shown.Text);
        Assert.Equal("Protect All", shown.Item2);
        Assert.Equal((380d, 160d), (shown.Width, shown.Height));          // the window's size is not part of this change
        Assert.Equal("Could not process this. Please copy again.", DecisionPromptCoordinator.NeutralFailureMessage);
    }

    [Fact]
    public void TheSettingsMessages_AreEnglish()
    {
        Assert.Equal("The request could not be processed. Please try again.", SettingsCoordinator.GenericFailureMessage);
        Assert.Equal("The value you entered could not be recognized. Please check it.", SettingsCoordinator.NoCandidateMessage);
        Assert.Equal("Please enter only one value.", SettingsCoordinator.MultipleCandidateMessage);
        Assert.Equal("The value does not match the selected type.", SettingsCoordinator.TypeMismatchMessage);
        Assert.Equal("Could not save the country setting. The previous setting remains in effect.", SettingsCoordinator.CountryChoiceFailureMessage);
        Assert.Equal("Saved settings are currently unavailable.\nRunning with protective defaults.", SettingsCoordinator.MasterKeyUnavailableBanner);
    }

    [Theory]
    [InlineData(UsOptInGate.NotSelected, true, "U.S. detection has not been enabled. U.S. detectors remain inactive until you enable this feature. Korean and Global detection continue to operate independently.")]
    [InlineData(UsOptInGate.Unavailable, false, "U.S. detection is unavailable because the official source data could not be verified.")]
    [InlineData(UsOptInGate.Unverifiable, true, "The U.S. detection enablement record could not be verified, so U.S. detection is off. This is not a choice you made; it is an unverified state. Enabling it again will record it anew.")]
    [InlineData(UsOptInGate.Unverifiable, false, "The U.S. detection enablement record could not be verified or was written by a newer version, so it cannot be changed. U.S. detection is off.")]
    public void TheSettingsUsNote_ForEachOptInState_IsTheExactEnglishText(UsOptInGate gate, bool editable, string expected)
    {
        var note = OnSta(() =>
        {
            var window = new SettingsWindow();
            window.RenderState(new SettingsViewState(
                PhoneEnabled: true, EmailEnabled: true, Exceptions: [], MasterKeyUnavailable: false, StatusMessage: null,
                UnitedStatesChoice: new UnitedStatesChoiceView(false, Privon.Storage.CountryChoiceState.Missing, editable, gate)));
            var block = (TextBlock)typeof(SettingsWindow).GetField("_unitedStatesNote", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var result = (block.Text, block.Visibility);
            window.Close();
            return result;
        });

        Assert.Equal(expected, note.Text);
        Assert.Equal(System.Windows.Visibility.Visible, note.Visibility);
    }

    [Theory]
    [InlineData(Privon.Storage.CountryChoiceState.Corrupt, "The saved country setting could not be verified, so the default (on) applies.")]
    [InlineData(Privon.Storage.CountryChoiceState.Unauthenticated, "The saved country setting could not be verified, so the default (on) applies.")]
    [InlineData(Privon.Storage.CountryChoiceState.UnknownVersion, "A country setting saved by a newer version exists and cannot be changed. The default (on) applies.")]
    public void TheSettingsCountryNote_ForAnUnreadableChoice_IsTheExactEnglishText(Privon.Storage.CountryChoiceState basis, string expected)
    {
        var note = OnSta(() =>
        {
            var window = new SettingsWindow();
            window.RenderState(new SettingsViewState(
                PhoneEnabled: true, EmailEnabled: true, Exceptions: [], MasterKeyUnavailable: false, StatusMessage: null,
                UnitedStatesChoice: new UnitedStatesChoiceView(true, basis, Editable: false)));
            var block = (TextBlock)typeof(SettingsWindow).GetField("_unitedStatesNote", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var result = block.Text;
            window.Close();
            return result;
        });

        Assert.Equal(expected, note);
    }

    [Fact]
    public void TheSettingsConfirmQuestions_AreEnglish()
    {
        string settings = File.ReadAllText(Path.Combine(Root(), "src", "Privon.App", "SettingsWindow.cs"));

        Assert.Contains("ConfirmThenRaise(\"Reset the protection scope to its defaults?\"", settings);
        Assert.Contains("ConfirmThenRaise(\"Reset all registered exceptions?\"", settings);
    }

    // ===== the protection status text: English, bounded, and still never a claim of blocking or of a verified rewrite ===============

    [Theory]
    [InlineData(ProtectionState.Verified)]
    [InlineData(ProtectionState.Ready)]
    [InlineData(ProtectionState.Scanning)]
    [InlineData(ProtectionState.NeedsDecision)]
    [InlineData(ProtectionState.Limited)]
    [InlineData(ProtectionState.Error)]
    public void EveryProtectionStatus_IsEnglish_FitsTheTooltip_AndOnlyVerifiedSaysRewriteVerified(ProtectionState state)
    {
        var p = ProtectionStatusPresenter.Present(new ClipboardProtectionStatus(state, AttemptId: 1, UnresolvedFailure: false, CompletedAttempts: 1));

        Assert.False(HasHangul(p.Tooltip + p.Detail));
        Assert.True(p.Tooltip.Length <= ProtectionStatusPresenter.MaxTooltipLength, p.Tooltip);
        Assert.Contains("does not currently block sending", p.Detail);
        Assert.Equal(state == ProtectionState.Verified, p.Tooltip.Contains("rewrite to protected form verified", StringComparison.Ordinal));
        Assert.DoesNotContain("blocked", p.Tooltip + p.Detail, StringComparison.OrdinalIgnoreCase);
        if (state is ProtectionState.Limited or ProtectionState.Error)
        {
            Assert.Contains("original text may remain on the clipboard", p.Detail);          // a failure never hides that the original may remain
            Assert.True(p.IsWarning);
        }
    }

    // ===== what this change does NOT touch: the clipboard output tokens and the detection ============================================

    [Fact]
    public void TheProtectedValueTokens_AreStillTheKoreanLabels_BecauseTheyAreADetectionContractNotUiText()
    {
        Assert.Equal("전화번호", AliasLabelProvider.GetLabel(PiiType.Phone));
        Assert.Equal("이메일", AliasLabelProvider.GetLabel(PiiType.Email));
        Assert.Equal("주소", AliasLabelProvider.GetLabel(PiiType.Address));
    }
}
