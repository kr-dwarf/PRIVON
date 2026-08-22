namespace Privon.Detection;

/// <summary>
/// The pipeline's output for one input text: the final set of candidates that survived
/// overlap resolution. Policy application (Trusted/Exception, alias assignment) is layered
/// on top in later phases -- Phase 2A stops at "which spans were detected, at what level
/// and confidence, with no overlaps."
///
/// DETECTION_CANONICAL_DIAGNOSTIC_SURFACE (Phase 3B STEP3.1): <see cref="ToString"/> is
/// explicitly overridden to print only the candidate count -- never recursively stringified
/// per-candidate. This is a defense-in-depth explicit override, not a reaction to an actual
/// leak: the record-synthesized ToString() for a collection-typed property calls the
/// collection's own ToString() (its runtime type name, e.g. <c>List`1[...]</c>), which does
/// not itself enumerate/print each element -- but relying on that incidental fact staying true
/// forever (e.g. if <see cref="Candidates"/>'s underlying collection type ever changed) is
/// exactly the kind of implicit safety this phase's contract forbids.
/// </summary>
public sealed record DetectionResult(IReadOnlyList<DetectionCandidate> Candidates)
{
    public override string ToString() => $"{nameof(DetectionResult)} {{ Count = {Candidates.Count} }}";
}
