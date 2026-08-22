using System.Windows;
using System.Windows.Controls;

namespace Privon.App;

/// <summary>
/// Phase 3C STEP40 -- the only production implementation of <see cref="IDecisionPromptSurface"/>.
/// Deliberately minimal: no dashboard, no settings, no clipboard/raw-text preview, no
/// <c>CanonicalValue</c> display, no PiiType display -- a single generic Korean message plus a
/// single "모두 보호" button (Phase 3C STEP39/STEP40's frozen MINIMUM_DECISION_ACTION). Built
/// entirely in code (no separate .xaml) to keep this one small, self-contained file the whole
/// surface. Must only ever be constructed on a thread with a live WPF <see cref="Dispatcher"/> --
/// i.e. only from <see cref="DecisionPromptCoordinator"/>'s dispatcher-marshaled callback, never
/// directly from a background thread.
///
/// <see cref="IDecisionPromptSurface.Closed"/> is implemented explicitly, forwarding to this
/// type's OWN base <see cref="Window.Closed"/> event -- so it fires identically whether this
/// window is closed programmatically (<see cref="Close"/>, e.g. because a newer scope superseded
/// it) or by the user's own system close button, without this type needing to distinguish the two
/// (Phase 3C STEP40 instruction's USER_CLOSING_THE_PROMPT: closing never resolves anything).
/// </summary>
internal sealed class DecisionPromptWindow : Window, IDecisionPromptSurface
{
    private readonly TextBlock _messageText;
    private readonly System.Windows.Controls.Button _protectAllButton;

    public DecisionPromptWindow()
    {
        Title = "PRIVON";
        Width = 380;
        Height = 160;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;

        _messageText = new TextBlock
        {
            Text = "개인정보가 감지되었습니다.\n클립보드 내용을 보호하려면 보호하기를 눌러주세요.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16, 16, 16, 8),
        };

        _protectAllButton = new System.Windows.Controls.Button
        {
            Content = "모두 보호",
            Margin = new Thickness(16, 0, 16, 16),
            Padding = new Thickness(12, 6, 12, 6),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        };
        _protectAllButton.Click += (_, _) => ProtectAllRequested?.Invoke(this, EventArgs.Empty);

        var panel = new StackPanel();
        panel.Children.Add(_messageText);
        panel.Children.Add(_protectAllButton);
        Content = panel;
    }

    public event EventHandler? ProtectAllRequested;

    event EventHandler? IDecisionPromptSurface.Closed
    {
        add => Closed += value;
        remove => Closed -= value;
    }

    public void DisableProtectAction() => _protectAllButton.IsEnabled = false;

    public void ShowNeutralFailure(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _messageText.Text = message;
        _protectAllButton.IsEnabled = false;
    }

    void IDecisionPromptSurface.Show() => Show();

    void IDecisionPromptSurface.Close() => Close();
}
