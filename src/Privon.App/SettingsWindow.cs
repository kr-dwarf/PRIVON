using System.Windows;
using System.Windows.Controls;
using Privon.Detection;

namespace Privon.App;

/// <summary>
/// PRIVON v0.2.1 Gate 3C -- the only production implementation of <see cref="ISettingsSurface"/>.
/// Built entirely in code (no separate .xaml), matching <see cref="DecisionPromptWindow"/>'s own
/// established style. UNLIKE <see cref="DecisionPromptWindow"/>, this window is a NORMAL, activating
/// window -- no <c>ShowActivated = false</c>, no <c>WS_EX_NOACTIVATE</c> -- it needs real keyboard
/// focus for exception-value entry (SETTINGS_ACTIVATES_NORMALLY, this Gate's own instruction).
///
/// INTENTIONALLY MINIMAL (this Gate's own UI DESIGN instruction): Protection Scope (Phone/Email
/// toggles only), User Exceptions (type selector limited to Phone/Email, one value field, Add,
/// list, Delete selected, Reset exceptions), Reset protection scope, and two independent optional
/// status/banner lines (see DEGRADED_STORAGE_STATUS_CORRECTION below) -- no dashboard, no
/// analytics, no extra settings, no fake detector categories (Name/Address/Company/IP/MAC/GPS never
/// appear as a selectable/interactive control anywhere in this type), no restoration/undo.
///
/// SOURCE_OF_TRUTH / RENDER_ONLY: this window holds only transient display state -- every value it
/// shows comes from the most recent <see cref="RenderState"/> call, driven entirely by
/// <see cref="SettingsCoordinator"/>. SUPPRESS_PROGRAMMATIC_REENTRY: setting a toggle's
/// <c>IsChecked</c> from <see cref="RenderState"/> would otherwise re-fire that toggle's own Click
/// handler (a standard WPF <c>ToggleButton</c> behavior) and re-raise
/// <see cref="PhoneToggleRequested"/>/<see cref="EmailToggleRequested"/> for a state the user never
/// actually requested -- <see cref="_suppressToggleEvents"/> guards against that.
///
/// DEGRADED_STORAGE_STATUS_CORRECTION (PRIVON v0.2.1 Gate 3C audit correction): the degraded-
/// storage explanation (<see cref="SettingsViewState.MasterKeyUnavailable"/>) and a transient
/// action/mutation-failure message (<see cref="SettingsViewState.StatusMessage"/>) are two
/// INDEPENDENT facts that may both be true at once -- <see cref="_degradedBannerText"/> and
/// <see cref="_statusText"/> are two separate <see cref="TextBlock"/> regions, each shown/hidden
/// from its own field only, in <see cref="RenderState"/>. A prior version of this type (and
/// <see cref="SettingsCoordinator"/>) let a non-null <c>StatusMessage</c> silently replace the
/// degraded-storage banner -- see <see cref="SettingsCoordinator"/>'s own class doc for the root
/// cause. Neither region is ever inferred from the other; <see cref="_degradedBannerText"/>'s own
/// fixed text is <see cref="SettingsCoordinator.MasterKeyUnavailableBanner"/> (the SAME approved
/// generic wording <see cref="SettingsCoordinator"/> already owns -- reused here, never duplicated
/// or paraphrased, and never derived from master-key/DPAPI/filesystem internals, which this window
/// never inspects).
///
/// PRIVACY_UI: the raw exception-value text field is cleared after every Add attempt (success or
/// rejection) -- never left echoing a value that may have just been rejected as a duplicate
/// candidate/type-mismatch -- and no PII value is ever placed into this window's <see cref="Window.Title"/>.
///
/// PRIVON 0.3.2 Gate 032-B1 -- BRANDING_ONLY: <see cref="Window.Icon"/> and a restrained header
/// wordmark image are now sourced from <see cref="BrandResources"/> (this assembly's own embedded
/// PNG/ICO resources, never a filesystem path). Purely visual -- no control behavior, no
/// provisioning/security state logic, and no architectural change to this window.
/// </summary>
internal sealed class SettingsWindow : Window, ISettingsSurface
{
    // CheckBox/RadioButton/TextBox/Button/ListBox are ambiguous between System.Windows.Controls and
    // System.Windows.Forms in this project (UseWPF + UseWindowsForms both true -- see
    // Privon.App.csproj) -- fully qualified here, matching DecisionPromptWindow's own established
    // precedent for System.Windows.Controls.Button.
    private readonly System.Windows.Controls.CheckBox _phoneToggle;
    private readonly System.Windows.Controls.CheckBox _emailToggle;
    private readonly System.Windows.Controls.RadioButton _typePhone;
    private readonly System.Windows.Controls.RadioButton _typeEmail;
    private readonly System.Windows.Controls.TextBox _valueBox;
    private readonly System.Windows.Controls.Button _addButton;
    private readonly System.Windows.Controls.ListBox _exceptionList;
    private readonly System.Windows.Controls.Button _deleteSelectedButton;
    private readonly System.Windows.Controls.Button _resetExceptionsButton;
    private readonly System.Windows.Controls.Button _resetProtectionScopeButton;

    // PRIVON 0.3.1 Gate E5G.1C -- the minimum functional Chrome Native Messaging setup surface.
    // _chromeStatusText always reflects the most recent SettingsViewState.ChromeNativeMessagingReadiness
    // (RENDER_ONLY, same discipline as every other control here); the two action buttons are
    // mutually-exclusive-visible per readiness (see RenderChromeSection) -- never both shown at once,
    // and never shown at all for Ready/ForeignBlocked/OrphanBlocked/Failed (no adopt/delete action
    // exists for a blocked or already-ready state).
    private readonly TextBlock _chromeStatusText;
    private readonly System.Windows.Controls.Button _chromeSetupButton;
    private readonly System.Windows.Controls.Button _chromeRepairButton;

    // PRIVON 0.3.1 Gate E5G.1G -- retained Edge presentation fields. The Chrome-only remediation
    // renders them as explicitly unavailable and exposes neither action while the shared release
    // policy defers Edge; the readiness-based rendering remains dormant for future reactivation.
    private readonly TextBlock _edgeStatusText;
    private readonly System.Windows.Controls.Button _edgeSetupButton;
    private readonly System.Windows.Controls.Button _edgeRepairButton;

    // DEGRADED_STORAGE_STATUS_CORRECTION (PRIVON v0.2.1 Gate 3C audit correction): two
    // INDEPENDENT regions, never one shared field -- MasterKeyUnavailable (degraded-storage
    // explanation) and StatusMessage (transient action/mutation feedback) are two distinct facts
    // that may both be true at once (SettingsViewState's own SOURCE_OF_TRUTH doc), so each gets its
    // own TextBlock, each shown/hidden from its own state field only. Neither rendering branch below
    // ever reads the other field -- that is precisely what let a mutation-failure StatusMessage
    // silently erase the degraded banner before this correction.
    private readonly TextBlock _degradedBannerText;
    private readonly TextBlock _statusText;

    private bool _suppressToggleEvents;

    public SettingsWindow()
    {
        Title = "PRIVON Settings";
        Width = 420;
        Height = 480;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = BrandResources.LoadWindowIconImage();

        // PRIVON 0.3.2 Gate 032-B1 -- restrained header brand treatment only: the official
        // horizontal PV + PRIVON wordmark, loaded from this assembly's own embedded resource
        // (never an absolute filesystem path), Stretch=Uniform with only Height constrained so its
        // native 385x120 aspect ratio is always preserved regardless of window width.
        var wordmark = new System.Windows.Controls.Image
        {
            Source = BrandResources.LoadWordmarkImage(),
            Height = 40,
            Stretch = System.Windows.Media.Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Margin = new Thickness(16, 12, 16, 4),
        };

        _degradedBannerText = new TextBlock
        {
            Text = SettingsCoordinator.MasterKeyUnavailableBanner,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16, 12, 16, 4),
            Foreground = System.Windows.Media.Brushes.DarkOrange,
            FontWeight = FontWeights.Bold,
            Visibility = Visibility.Collapsed,
        };

        _statusText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16, 4, 16, 4),
            Foreground = System.Windows.Media.Brushes.DarkRed,
            Visibility = Visibility.Collapsed,
        };

        var scopeHeader = new TextBlock { Text = "Protection Scope", FontWeight = FontWeights.Bold, Margin = new Thickness(16, 8, 16, 4) };
        _phoneToggle = new System.Windows.Controls.CheckBox { Content = "Phone", Margin = new Thickness(16, 2, 16, 2) };
        _emailToggle = new System.Windows.Controls.CheckBox { Content = "Email", Margin = new Thickness(16, 2, 16, 2) };
        _phoneToggle.Click += (_, _) => { if (!_suppressToggleEvents) PhoneToggleRequested?.Invoke(this, _phoneToggle.IsChecked == true); };
        _emailToggle.Click += (_, _) => { if (!_suppressToggleEvents) EmailToggleRequested?.Invoke(this, _emailToggle.IsChecked == true); };

        _resetProtectionScopeButton = new System.Windows.Controls.Button { Content = "Reset protection scope", Margin = new Thickness(16, 4, 16, 8), HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        _resetProtectionScopeButton.Click += (_, _) => ConfirmThenRaise("보호 범위를 기본값으로 초기화하시겠습니까?", () => ResetProtectionScopeRequested?.Invoke(this, EventArgs.Empty));

        var exceptionsHeader = new TextBlock { Text = "User Exceptions", FontWeight = FontWeights.Bold, Margin = new Thickness(16, 8, 16, 4) };

        _typePhone = new System.Windows.Controls.RadioButton { Content = "Phone", GroupName = "ExceptionType", IsChecked = true, Margin = new Thickness(0, 0, 12, 0) };
        _typeEmail = new System.Windows.Controls.RadioButton { Content = "Email", GroupName = "ExceptionType" };
        var typePanel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(16, 0, 16, 4) };
        typePanel.Children.Add(_typePhone);
        typePanel.Children.Add(_typeEmail);

        _valueBox = new System.Windows.Controls.TextBox { Margin = new Thickness(16, 0, 16, 4) };
        _addButton = new System.Windows.Controls.Button { Content = "Add", Margin = new Thickness(16, 0, 16, 8), HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        _addButton.Click += (_, _) => RaiseAddException();

        _exceptionList = new System.Windows.Controls.ListBox { Margin = new Thickness(16, 0, 16, 4), Height = 140 };

        var listButtons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(16, 0, 16, 12) };
        _deleteSelectedButton = new System.Windows.Controls.Button { Content = "Delete selected", Margin = new Thickness(0, 0, 8, 0) };
        _deleteSelectedButton.Click += (_, _) => RaiseDeleteSelected();
        _resetExceptionsButton = new System.Windows.Controls.Button { Content = "Reset exceptions" };
        _resetExceptionsButton.Click += (_, _) => ConfirmThenRaise("등록된 예외를 모두 초기화하시겠습니까?", () => ResetExceptionsRequested?.Invoke(this, EventArgs.Empty));
        listButtons.Children.Add(_deleteSelectedButton);
        listButtons.Children.Add(_resetExceptionsButton);

        // PRIVON 0.3.2 Gate 032-C2 -- "Chrome Protection" / "Connect Chrome" / "Reconnect Chrome":
        // ordinary-user wording only, never Native Messaging/registry/manifest/host/Setup/Repair
        // terminology. Edge's own header/button text below remains completely unchanged (Edge stays
        // release-gated off in 0.3.x; this gate is Chrome-only).
        var chromeHeader = new TextBlock { Text = "Chrome Protection", FontWeight = FontWeights.Bold, Margin = new Thickness(16, 8, 16, 4) };
        _chromeStatusText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 0, 16, 4) };
        _chromeSetupButton = new System.Windows.Controls.Button { Content = "Connect Chrome", Margin = new Thickness(16, 0, 16, 4), HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
        _chromeSetupButton.Click += (_, _) => ChromeNativeMessagingProvisionRequested?.Invoke(this, EventArgs.Empty);
        _chromeRepairButton = new System.Windows.Controls.Button { Content = "Reconnect Chrome", Margin = new Thickness(16, 0, 16, 8), HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
        _chromeRepairButton.Click += (_, _) => ChromeNativeMessagingRepairRequested?.Invoke(this, EventArgs.Empty);

        var edgeHeader = new TextBlock { Text = "Edge Web/AI Protection Setup", FontWeight = FontWeights.Bold, Margin = new Thickness(16, 8, 16, 4) };
        _edgeStatusText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 0, 16, 4) };
        _edgeSetupButton = new System.Windows.Controls.Button { Content = "Set up", Margin = new Thickness(16, 0, 16, 4), HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
        _edgeSetupButton.Click += (_, _) => EdgeNativeMessagingProvisionRequested?.Invoke(this, EventArgs.Empty);
        _edgeRepairButton = new System.Windows.Controls.Button { Content = "Repair", Margin = new Thickness(16, 0, 16, 8), HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
        _edgeRepairButton.Click += (_, _) => EdgeNativeMessagingRepairRequested?.Invoke(this, EventArgs.Empty);

        var panel = new StackPanel();
        panel.Children.Add(wordmark);
        panel.Children.Add(_degradedBannerText);
        panel.Children.Add(_statusText);
        panel.Children.Add(scopeHeader);
        panel.Children.Add(_phoneToggle);
        panel.Children.Add(_emailToggle);
        panel.Children.Add(_resetProtectionScopeButton);
        panel.Children.Add(exceptionsHeader);
        panel.Children.Add(typePanel);
        panel.Children.Add(_valueBox);
        panel.Children.Add(_addButton);
        panel.Children.Add(_exceptionList);
        panel.Children.Add(listButtons);
        panel.Children.Add(chromeHeader);
        panel.Children.Add(_chromeStatusText);
        panel.Children.Add(_chromeSetupButton);
        panel.Children.Add(_chromeRepairButton);
        panel.Children.Add(edgeHeader);
        panel.Children.Add(_edgeStatusText);
        panel.Children.Add(_edgeSetupButton);
        panel.Children.Add(_edgeRepairButton);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    event EventHandler? ISettingsSurface.Closed
    {
        add => Closed += value;
        remove => Closed -= value;
    }

    public event EventHandler<bool>? PhoneToggleRequested;
    public event EventHandler<bool>? EmailToggleRequested;
    public event EventHandler? ResetProtectionScopeRequested;
    public event EventHandler<AddExceptionRequest>? AddExceptionRequested;
    public event EventHandler<UserExceptionValue>? DeleteExceptionRequested;
    public event EventHandler? ResetExceptionsRequested;
    public event EventHandler? ChromeNativeMessagingProvisionRequested;
    public event EventHandler? ChromeNativeMessagingRepairRequested;
    public event EventHandler? EdgeNativeMessagingProvisionRequested;
    public event EventHandler? EdgeNativeMessagingRepairRequested;

    void ISettingsSurface.Show() => Show();

    // SETTINGS_ACTIVATES_NORMALLY: a genuine foreground activation -- restores from a minimized
    // state first (a real user re-opening Settings from the tray expects it to actually come to
    // the front, not merely flash in the taskbar).
    void ISettingsSurface.Activate()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
    }

    void ISettingsSurface.Close() => Close();

    public void RenderState(SettingsViewState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        _suppressToggleEvents = true;
        try
        {
            _phoneToggle.IsChecked = state.PhoneEnabled;
            _emailToggle.IsChecked = state.EmailEnabled;
        }
        finally
        {
            _suppressToggleEvents = false;
        }

        _exceptionList.Items.Clear();
        foreach (var entry in state.Exceptions)
        {
            _exceptionList.Items.Add(new ExceptionListItem(entry));
        }

        // DEGRADED_STORAGE_STATUS_CORRECTION: each region reads ONLY its own state field -- never
        // "else" against the other's presence, and never any precedence between them. Both can be
        // visible at once; either, neither, or both independently.
        _degradedBannerText.Visibility = state.MasterKeyUnavailable ? Visibility.Visible : Visibility.Collapsed;

        if (string.IsNullOrEmpty(state.StatusMessage))
        {
            _statusText.Visibility = Visibility.Collapsed;
            _statusText.Text = string.Empty;
        }
        else
        {
            _statusText.Text = state.StatusMessage;
            _statusText.Visibility = Visibility.Visible;
        }

        // PRIVACY_UI: the raw entry field is always cleared on a fresh render -- never left echoing
        // a value from a prior (possibly rejected) attempt.
        _valueBox.Clear();

        RenderChromeSection(state.ChromeNativeMessagingReadiness);
        RenderEdgeSection(state.EdgeNativeMessagingReadiness);
    }

    // PRIVON 0.3.1 Gate E5G.1C -- no registry path, manifest content, or other internal mechanics
    // ever reaches this text; each readiness maps to one short, generic status line. The two action
    // buttons are each visible ONLY for the one readiness they are meaningful for -- Ready/
    // ForeignBlocked/OrphanBlocked/Failed show neither (no adopt/delete/silent-repair action exists
    // for any of those).
    private void RenderChromeSection(NativeMessagingRegistrationReadiness readiness)
    {
        // PRIVON 0.3.2 Gate 032-C2 -- Fresh/Ready/OwnedNeedsRepair now use ordinary-user connection
        // language ("connected"/"not connected"/"connection needs updating") instead of Setup/Repair
        // terminology. ForeignBlocked/OrphanBlocked/default wording is UNCHANGED (section 7: existing
        // diagnostic wording may remain).
        _chromeStatusText.Text = readiness switch
        {
            NativeMessagingRegistrationReadiness.Fresh => "Chrome: not connected",
            NativeMessagingRegistrationReadiness.Ready => "Chrome: connected",
            NativeMessagingRegistrationReadiness.OwnedNeedsRepair => "Chrome: connection needs updating",
            NativeMessagingRegistrationReadiness.ForeignBlocked => "Chrome: blocked (already used by another program)",
            NativeMessagingRegistrationReadiness.OrphanBlocked => "Chrome: blocked (conflicting file present)",
            _ => "Chrome: unavailable",
        };

        _chromeSetupButton.Visibility = readiness == NativeMessagingRegistrationReadiness.Fresh
            ? Visibility.Visible : Visibility.Collapsed;
        _chromeRepairButton.Visibility = readiness == NativeMessagingRegistrationReadiness.OwnedNeedsRepair
            ? Visibility.Visible : Visibility.Collapsed;
    }

    // PRIVON 0.3.1 Chrome-only remediation: release scope overrides underlying Edge readiness.
    private void RenderEdgeSection(NativeMessagingRegistrationReadiness readiness)
    {
        if (!ReleaseBrowserSupportPolicy.IsSupported(NativeMessagingBrowser.Edge))
        {
            _edgeStatusText.Text = "Edge: unavailable";
            _edgeSetupButton.Visibility = Visibility.Collapsed;
            _edgeRepairButton.Visibility = Visibility.Collapsed;
            return;
        }

        _edgeStatusText.Text = readiness switch
        {
            NativeMessagingRegistrationReadiness.Fresh => "Edge: not set up",
            NativeMessagingRegistrationReadiness.Ready => "Edge: ready",
            NativeMessagingRegistrationReadiness.OwnedNeedsRepair => "Edge: needs repair",
            NativeMessagingRegistrationReadiness.ForeignBlocked => "Edge: blocked (already used by another program)",
            NativeMessagingRegistrationReadiness.OrphanBlocked => "Edge: blocked (conflicting file present)",
            _ => "Edge: unavailable",
        };

        _edgeSetupButton.Visibility = readiness == NativeMessagingRegistrationReadiness.Fresh
            ? Visibility.Visible : Visibility.Collapsed;
        _edgeRepairButton.Visibility = readiness == NativeMessagingRegistrationReadiness.OwnedNeedsRepair
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RaiseAddException()
    {
        var selectedType = _typePhone.IsChecked == true ? PiiType.Phone : PiiType.Email;
        AddExceptionRequested?.Invoke(this, new AddExceptionRequest(selectedType, _valueBox.Text));
    }

    private void RaiseDeleteSelected()
    {
        if (_exceptionList.SelectedItem is ExceptionListItem item)
        {
            DeleteExceptionRequested?.Invoke(this, item.Value);
        }
    }

    private void ConfirmThenRaise(string question, Action raise)
    {
        var result = System.Windows.MessageBox.Show(this, question, "PRIVON", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Yes)
        {
            raise();
        }
    }

    // Wraps a UserExceptionValue for ListBox display -- ToString projects only PiiType, never the
    // canonical value, matching this window's own explicit product-decision override below.
    private sealed class ExceptionListItem(UserExceptionValue value)
    {
        public UserExceptionValue Value { get; } = value;

        // PRIVON v0.2.1 Gate 3C EXPLICIT_PRODUCT_DECISION: this IS the authorized explicit Settings
        // exception-management list -- the ONE place this codebase deliberately displays the
        // registered canonical value (see SettingsCoordinator's own class doc / this Gate's own
        // EXCEPTION_LIST_DISPLAY instruction). Every other diagnostic/log/error/title surface in
        // this codebase continues to withhold it.
        public override string ToString() => $"{Value.PiiType}: {Value.CanonicalValue.Value}";
    }
}
