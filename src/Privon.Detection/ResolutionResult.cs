namespace Privon.Detection;

/// <summary>D3B0-A A.9 §23.3 -- the resolver's input.</summary>
public sealed record ResolutionInput(IReadOnlyList<DetectionCandidate> Candidates, int RawInputLength);

/// <summary>
/// D3B0-A A.9 §23.3 / §11.6 -- one suppressed candidate with its provenance. Only the SELECTED
/// suppressor is recorded (SUPPRESSION_PROVENANCE_SELECTOR).
/// </summary>
public sealed record SuppressedCandidate(
    DetectionCandidate Candidate, SuppressionReason Reason, DetectionCandidate SuppressedBy);

/// <summary>
/// D3B0-A A.9 §23.3 / §14.2 -- winners and suppressed records, each in the frozen R6 order.
/// R6 output ordering is NOT arbitration: it never removes a candidate and never changes a winner.
/// </summary>
public sealed record ResolutionResult(
    IReadOnlyList<DetectionCandidate> Winners,
    IReadOnlyList<SuppressedCandidate> Suppressed);
