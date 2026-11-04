using Privon.UsProductIntegration;

namespace Privon.App;

// [PRIVON-AI-HANDOFF]
// ROLE: The user-visible U.S. detector READINESS: a plain data report from an explicit opt-in source, and a pure presenter that turns it into the
//       tray menu line and its detail.
// TRUTH: "Ready" means a detector can run. It is NOT Verified (that is one clipboard attempt's checked rewrite), it is not protection of anything,
//        and the product cannot block a paste or a send. A report only ever shows what a source states NOW; nothing here remembers an earlier one.
// FROZEN: The text never implies the current input is safe or protected, never says a send was blocked, and a synthetic or shape-only authority is
//         never shown as production-verified data.
// DO_NOT: Reference the U.S. detector implementation beyond its gate enum (the source is injected as a delegate), infer 11/13 when no source was supplied, cache a report, carry any
//         clipboard text (a report holds detector ids and enums only), or notify with a balloon (the protection status owns warnings).
// NAVIGATE: PrivonAppUiBridge reads the source on the UI thread at render time; WinFormsTrayIconSurface only displays the presentation.

/// <summary>Where the authority behind the phone / address detectors came from.</summary>
public enum UsAuthorityProvenance
{
    /// <summary>No authority was selected: the two authority-backed detectors have nothing to run on.</summary>
    NotSelected = 0,

    /// <summary>The official-source production artifact, checked against the full production scope.</summary>
    Production,

    /// <summary>A caller-supplied authority checked for shape only (tests, fixtures): NOT production data.</summary>
    Provided,
}

/// <summary>Why a detector can or cannot run. <see cref="NotProvided"/> is the zero value: nothing is ready unless a source says so.</summary>
public enum UsDetectorAvailability
{
    NotProvided = 0,
    ValidationFailed,
    Ready,
}

public sealed record UsDetectorReadiness(string DetectorId, UsDetectorAvailability Availability);

/// <summary>
/// What an explicit U.S. opt-in source states about its detectors right now. <paramref name="UsEnabled"/> is whatever the opt-in pipeline follows
/// (the product has no authenticated country setting: this does not claim one). Holds detector ids and enums only.
/// </summary>
public sealed record UsReadinessReport(
    bool UsEnabled, UsAuthorityProvenance Provenance, IReadOnlyList<UsDetectorReadiness> Detectors, string? ArtifactVersion = null,
    UsOptInGate Gate = UsOptInGate.NotGated);

/// <summary>What the tray shows: one menu line and its longer detail. <see cref="UsReadinessKind.Unknown"/> when the current state could not be determined.</summary>
internal sealed record UsReadinessPresentation(UsReadinessKind Kind, string MenuText, string Detail);

internal enum UsReadinessKind
{
    Unknown,
    Off,
    AllReady,
    Partial,

    /// <summary>The shipped opt-in gate (P1): the user has not turned U.S. detection on, so it does not run. NOT the same as <see cref="Off"/>, a stored choice.</summary>
    NotSelected,

    /// <summary>The gate's activation record cannot be verified, so U.S. detection stays off. Never shown as a normal OFF choice.</summary>
    Unverifiable,

    /// <summary>The verified production authority is not fully usable: U.S. detection is not offered and nothing is called ready.</summary>
    Unavailable,
}

/// <summary>The tray's persistent readiness entry (a menu line the user can open at any time). Implemented in addition to, never instead of, the
/// protection status surface: a readiness line never changes the tooltip and never raises a balloon.</summary>
internal interface IUsReadinessSurface
{
    /// <summary>Raised when the user opens the menu: the bridge then re-reads the source, so the line is never older than the moment it is looked at.</summary>
    event EventHandler? UsReadinessRefreshRequested;

    void SetUsReadiness(UsReadinessPresentation presentation);
}

internal static class UsReadinessPresenter
{
    public const int ExpectedDetectorCount = 13;

    private static readonly (string Id, string Name)[] Approved =
    [
        ("us.atin", "ATIN"), ("us.itin", "ITIN"), ("us.ssn", "SSN"), ("us.mbi", "MBI"), ("us.sevis", "SEVIS"),
        ("us.passport", "Passport"), ("us.a_number", "A-Number"), ("us.i94", "I-94"),
        ("us.driver_state_id", "Driver's License / State ID"), ("us.bank_account", "Bank Account"),
        ("us.phone", "Phone"), ("us.address", "Address"), ("us.name", "Name"),
    ];

    private const string ReadyIsNotProtection =
        "‘Ready’ means a detector can run. It is not protection of any clipboard item, nor a verified rewrite (Verified). " +
        "This display does not guarantee that sending is safe, and PRIVON does not currently block sending.";

    public static UsReadinessPresentation Unknown() => new(
        UsReadinessKind.Unknown,
        "U.S. Detection: Readiness Unknown",
        "The current readiness of the U.S. detectors could not be determined. Do not rely on a previously displayed state as the current one. " + ReadyIsNotProtection);

    public static UsReadinessPresentation Present(UsReadinessReport? report)
    {
        if (report is null || report.Detectors is null || !Enum.IsDefined(report.Provenance)) return Unknown();

        // The shipped opt-in gate (P1) is decided before anything about detectors: no U.S. detector exists until the user has activated U.S. detection.
        switch (report.Gate)
        {
            case UsOptInGate.Unavailable:
                return new(UsReadinessKind.Unavailable, "U.S. Detection: Unavailable (Official Source Verification Failed)",
                    "The official source data could not be verified, so U.S. detection is not provided. It is not shown as ready, and U.S. detectors do not run. Korean and Global detection continue to operate independently. " + ReadyIsNotProtection);
            case UsOptInGate.NotSelected:
                return new(UsReadinessKind.NotSelected, "U.S. Detection: Not Enabled (Korean and Global detection remain active)",
                    "U.S. detection has not been enabled. U.S. detectors remain inactive until you enable this feature in Settings. Korean and Global detection continue to operate independently. " + ReadyIsNotProtection);
            case UsOptInGate.Unverifiable:
                return new(UsReadinessKind.Unverifiable, "U.S. Detection: Off · Enablement Record Unverifiable",
                    "The saved U.S. detection enablement record could not be verified, so U.S. detection was not turned on. This is not a choice you made; it is an unverified state. You can enable it again in Settings. Korean and Global detection continue to operate independently. " + ReadyIsNotProtection);
        }

        if (!report.UsEnabled)
        {
            return new(UsReadinessKind.Off, "U.S. Detection: Off (Korean and Global detection remain active)",
                "U.S. personal information detection is off. Korean and Global detection continue to operate independently. " + ReadyIsNotProtection);
        }

        // The report must name exactly the thirteen approved detectors once each; anything else is not a state we can describe.
        var byId = new Dictionary<string, UsDetectorAvailability>(StringComparer.Ordinal);
        foreach (var d in report.Detectors)
        {
            if (d is null || string.IsNullOrEmpty(d.DetectorId) || !Enum.IsDefined(d.Availability) || !byId.TryAdd(d.DetectorId, d.Availability))
                return Unknown();
        }

        if (byId.Count != ExpectedDetectorCount || Approved.Any(a => !byId.ContainsKey(a.Id))) return Unknown();

        // A pass of the authority-backed detectors without any authority is a contradiction, not a state to show.
        bool authorityBacked(string id) => id is "us.phone" or "us.address";
        if (report.Provenance == UsAuthorityProvenance.NotSelected
            && Approved.Any(a => authorityBacked(a.Id) && byId[a.Id] == UsDetectorAvailability.Ready))
            return Unknown();

        int ready = Approved.Count(a => byId[a.Id] == UsDetectorAvailability.Ready);
        var notProvided = Approved.Where(a => byId[a.Id] == UsDetectorAvailability.NotProvided).Select(a => a.Name).ToList();
        var failed = Approved.Where(a => byId[a.Id] == UsDetectorAvailability.ValidationFailed).Select(a => a.Name).ToList();

        if (ready == ExpectedDetectorCount)
        {
            return report.Provenance == UsAuthorityProvenance.Production
                ? new(UsReadinessKind.AllReady, "U.S. Detection: 13/13 Ready (Verified Official Sources)",
                    "All 13 U.S. detectors are ready to run. Phone and address detection use an Authority verified against official sources"
                    + (string.IsNullOrEmpty(report.ArtifactVersion) ? "" : $" ({report.ArtifactVersion})") + ". " + ReadyIsNotProtection)
                : new(UsReadinessKind.AllReady, "U.S. Detection: 13/13 Ready (Test Data · Not Production-Verified)",
                    "All 13 U.S. detectors are ready to run, but the phone and address data are not verified against official sources. " + ReadyIsNotProtection);
        }

        var segments = new List<string>();
        if (notProvided.Count > 0) segments.Add($"Data not selected: {string.Join(", ", notProvided)}");
        if (failed.Count > 0) segments.Add($"Unavailable (data validation failed): {string.Join(", ", failed)}");

        return new(UsReadinessKind.Partial,
            $"U.S. Detection: {ready}/{ExpectedDetectorCount} Ready · " + string.Join(" · ", segments),
            $"Only {ready} of {ExpectedDetectorCount} U.S. detectors are ready to run. " + string.Join(". ", segments)
            + ". Detectors that are not ready do not detect anything. " + ReadyIsNotProtection);
    }
}
