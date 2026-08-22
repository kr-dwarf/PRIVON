namespace Privon.App;

/// <summary>
/// Phase 3C STEP41 -- the narrow seam <see cref="ClipboardPrivacyCoordinator"/> uses to observe its
/// own real production path, purely for real-environment manual QA diagnosis. Deliberately narrow
/// (one method) so a hand-written fake can assert exactly what was recorded without a mocking
/// framework, and so the production implementation
/// (<see cref="FileClipboardDiagnosticRecorder"/>)/no-op implementation
/// (<see cref="NullClipboardDiagnosticRecorder"/>) are trivially interchangeable.
///
/// CONTRACT (frozen for this STEP): <see cref="Record"/> must never throw, must never block the
/// caller for any meaningful duration, must never perform synchronous file/network I/O on the
/// calling thread, and must never acquire any lock <c>ClipboardPrivacyCoordinator</c> itself
/// already holds (the clipboard owner thread's callback and the App-level operation gate both
/// depend on this). <c>ClipboardPrivacyCoordinator</c> additionally wraps every call to this method
/// in its own defensive <c>try</c>/<c>catch</c> (see its own <c>Diagnose</c> helper) -- so even an
/// implementation that violates the no-throw half of this contract can never affect protection
/// processing; this method's own contract exists so a correct implementation does not depend on
/// that defensive wrapper for correctness, only for absolute safety margin.
/// </summary>
internal interface IClipboardDiagnosticRecorder
{
    void Record(ClipboardDiagnosticEvent diagnosticEvent);
}
