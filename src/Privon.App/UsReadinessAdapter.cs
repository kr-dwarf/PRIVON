using Privon.UsProductIntegration;

namespace Privon.App;

// [PRIVON-AI-HANDOFF]
// ROLE: The one mapping from what the U.S. opt-in owner states (UsOptInReadinessSnapshot, read from the registry of the pipeline in use) to the report the
//       tray line understands. Shared by the shipped startup and the experimental host, so the two can never describe the same state differently.
// TRUTH: 1:1 -- same detectors, same availability, same provenance, same artifact version, same opt-in gate. It counts nothing and adds nothing.
// FROZEN: An unknown enum value maps to the NON-ready / NOT-production side, never the other way.
// DO_NOT: Compute a count here, infer readiness from anything but the snapshot, or promote a Provided authority to Production.

public static class UsReadinessAdapter
{
    public static UsReadinessReport From(UsOptInReadinessSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new UsReadinessReport(
            snapshot.UsEnabled,
            snapshot.Provenance switch
            {
                UsOptInProvenance.Production => UsAuthorityProvenance.Production,
                UsOptInProvenance.Provided => UsAuthorityProvenance.Provided,
                _ => UsAuthorityProvenance.NotSelected,
            },
            snapshot.Detectors.Select(d => new UsDetectorReadiness(d.DetectorId, d.Availability switch
            {
                UsOptInAvailability.Ready => UsDetectorAvailability.Ready,
                UsOptInAvailability.ValidationFailed => UsDetectorAvailability.ValidationFailed,
                _ => UsDetectorAvailability.NotProvided,
            })).ToList(),
            snapshot.ArtifactVersion,
            Enum.IsDefined(snapshot.Gate) ? snapshot.Gate : UsOptInGate.Unavailable);
    }
}
