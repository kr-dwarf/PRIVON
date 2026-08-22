namespace Privon.App;

/// <summary>
/// Phase 3C STEP41 -- the default <see cref="IClipboardDiagnosticRecorder"/> every
/// <see cref="ClipboardPrivacyCoordinator"/> uses when no diagnostic recorder is supplied (the
/// existing 9-/10-argument constructor overloads every pre-STEP41 caller and test already uses). A
/// pure no-op -- <see cref="Record"/> does nothing at all, so a coordinator built without STEP41's
/// new optional constructor argument behaves, in every observable way, exactly as it did before
/// this STEP (see <c>ClipboardDiagnostic_DisabledProducesIdenticalBehavior</c> for the regression
/// proving this).
/// </summary>
internal sealed class NullClipboardDiagnosticRecorder : IClipboardDiagnosticRecorder
{
    public static readonly NullClipboardDiagnosticRecorder Instance = new();

    private NullClipboardDiagnosticRecorder()
    {
    }

    public void Record(ClipboardDiagnosticEvent diagnosticEvent)
    {
        // Deliberately empty.
    }
}
