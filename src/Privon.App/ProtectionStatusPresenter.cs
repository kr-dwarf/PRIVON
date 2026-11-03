using Privon.Core;

namespace Privon.App;

// [PRIVON-AI-HANDOFF]
// ROLE: Pure mapping from the ClipboardProtectionStatus to the tray text and the warning notification decision.
// TRUTH: Nothing here claims a paste or a send was blocked (the product cannot); Verified is scoped to the LAST processed attempt.
// FROZEN: Error and Limited always say the protection is unconfirmed, the text may remain, and PRIVON does not block sending.
// DO_NOT: Add text that implies the current input is safe, or an alert for a repeated identical failure.
// NAVIGATE: PrivonAppUiBridge calls this on the UI thread; WinFormsTrayIconSurface only displays the result.

/// <summary>What the tray shows for one status. <see cref="Tooltip"/> is bounded for NotifyIcon; <see cref="Detail"/> is the full text.</summary>
internal sealed record ProtectionStatusPresentation(ProtectionState State, string Tooltip, string Detail, bool IsWarning);

internal static class ProtectionStatusPresenter
{
    /// <summary>NotifyIcon.Text throws above 63 characters.</summary>
    public const int MaxTooltipLength = 63;

    /// <summary>The user guidance for a failed scan, as required: unconfirmed protection, the text may remain, no blocking.</summary>
    public const string UnconfirmedWarning =
        "Protection of personal information could not be confirmed. The original text may remain on the clipboard, and PRIVON does not currently block sending.";

    private const string LimitedWarning =
        "Protection of personal information could not be fully confirmed. The original text may remain on the clipboard, and PRIVON does not currently block sending.";

    private const string NoBlockingNote = "This status does not guarantee that sending is safe, and PRIVON does not currently block sending.";

    public static ProtectionStatusPresentation Present(ClipboardProtectionStatus status)
    {
        // An unresolved earlier failure is never hidden by a later, unfinished state.
        if (status.UnresolvedFailure && status.State is not (ProtectionState.Error or ProtectionState.Limited))
        {
            return status.State == ProtectionState.Scanning
                ? new(status.State, "PRIVON — Scanning, earlier error unresolved",
                    "Scanning is in progress, but an earlier processing error is still unresolved. " + UnconfirmedWarning, true)
                : Warning(status.State, "PRIVON — Protection unconfirmed, original may remain", UnconfirmedWarning);
        }

        // Ready is not always "a scan finished". A copy in an app PRIVON does not support, or one with no text, is never scanned; and before the
        // first clipboard change nothing has been checked at all. Those are stated as what they are: the headline is the fact that nothing was
        // scanned, and the separate fact that nothing is blocked stays in the long text. (An earlier failure never gets here: its state is Limited
        // or Error, handled above.)
        if (status.State == ProtectionState.Ready)
        {
            // The user chose to keep the original. That is a statement about the user's choice only: the one-time pass the choice issued is not known
            // to have been used (nothing in the product evaluates it), so this is never worded as finished, protected or verified.
            if (status.DecisionKeptOriginal)
                return new(status.State, "PRIVON — Original kept by choice, item not protected",
                    "The item you chose to keep as original is not protected, and the original text remains on the clipboard. The one-time allowance has not been confirmed as used. " + NoBlockingNote, false);
            if (status.LastNotScanned == UnscannedReason.UnsupportedTarget)
                return new(status.State, "PRIVON — Copied from unsupported app, not scanned",
                    "The last copy was made in an app PRIVON does not support, so it was not scanned. The copied content is unchanged, and the original text may remain on the clipboard. " + NoBlockingNote, false);
            if (status.LastNotScanned == UnscannedReason.NonText)
                return new(status.State, "PRIVON — Non-text copy, not scanned",
                    "The last copy was not text, so it was not scanned. " + NoBlockingNote, false);
            if (status.CompletedAttempts == 0)
                return new(status.State, "PRIVON — Running, nothing scanned yet",
                    "No clipboard content has been scanned yet. " + NoBlockingNote, false);
        }

        return status.State switch
        {
            ProtectionState.Error => Warning(status.State, "PRIVON — Protection unconfirmed, original may remain", UnconfirmedWarning),
            ProtectionState.Limited => Warning(status.State, "PRIVON — Protection not fully confirmed", LimitedWarning),
            ProtectionState.NeedsDecision => new(status.State, "PRIVON — Awaiting your decision, not yet protected",
                "Waiting for your decision; the content is not yet protected. The original text remains on the clipboard. " + NoBlockingNote, false),
            ProtectionState.Scanning => new(status.State, "PRIVON — Scanning, not yet protected",
                "Scanning is in progress; the content is not yet protected. " + NoBlockingNote, false),
            ProtectionState.Ready => new(status.State, "PRIVON — Scan finished, send safety not guaranteed",
                "The last scan has finished. " + NoBlockingNote, false),
            ProtectionState.Verified => new(status.State, "PRIVON — Last rewrite to protected form verified",
                "In the last processing attempt, the rewrite to the protected form was confirmed (written and read back). Later input must be checked again. " + NoBlockingNote, false),

            // Blocked, Paused and Initializing are never produced by the tracker, and the product has no blocking: if one ever
            // reaches the display it is shown as unconfirmed, never as safe and never as a blocked transmission.
            _ => Warning(status.State, "PRIVON — Protection status unknown", UnconfirmedWarning),
        };
    }

    /// <summary>
    /// One warning notification per failure episode: when a warning begins, or when Limited escalates to Error. A repeated
    /// identical failure, the scan between two failures, a de-escalation and a recovery do not alert again; a new failure
    /// after a real recovery does.
    /// </summary>
    public static bool ShouldNotify(ProtectionStatusPresentation? previous, ProtectionStatusPresentation current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!current.IsWarning) return false;
        if (previous is null || !previous.IsWarning) return true;
        return previous.State == ProtectionState.Limited && current.State == ProtectionState.Error;
    }

    private static ProtectionStatusPresentation Warning(ProtectionState state, string tooltip, string detail) =>
        new(state, tooltip, detail, true);
}
