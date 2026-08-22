namespace Privon.App;

/// <summary>
/// Phase 3B STEP14 -- the smallest sensitive carrier needed to move Alias-replaced clipboard text
/// from the synchronous <see cref="ClipboardPrivacyProcessor"/> to the async
/// <see cref="ClipboardPrivacyCoordinator"/>'s own guarded-write call. Deliberately carries ONLY
/// the replacement text -- no target token (the coordinator already has the exact
/// <c>ForegroundTargetSnapshot</c> that authorized the guarded read; see TARGET_TOKEN_FLOW), no
/// sequence token (the coordinator already has the successful read's own
/// <c>ClipboardTextSnapshot.SequenceNumber</c>; see SEQUENCE_TOKEN_FLOW), no
/// <c>PiiType</c>/<c>DetectionResult</c>/<c>CandidatePolicyDecision</c>/<c>AliasAssignment</c>
/// list/<c>CanonicalValue</c> of any kind.
///
/// Exists only for the duration of one processing attempt -- never a field on
/// <see cref="ClipboardPrivacyProcessor"/> or <see cref="ClipboardPrivacyCoordinator"/> (see both
/// types' own structural "no raw/sensitive field" tests). Once the attempt's single write call
/// (or its decision not to write) completes, the only reference to this instance -- a local
/// variable in <c>ClipboardPrivacyCoordinator.ProcessNotificationAsync</c> -- goes out of scope
/// and the instance becomes GC-eligible. This is a reference-discard/GC-eligibility claim only,
/// never a claim of deterministic memory zeroization (same discipline as every other sensitive
/// local documented throughout this codebase, e.g. <c>ClipboardPrivacyProcessor.ProcessWithAssignments</c>'s
/// own doc).
///
/// <see cref="ToString"/> is explicitly overridden to exclude <see cref="ReplacementText"/>
/// entirely -- metadata only, matching the RAW_CLIPBOARD_DIAGNOSTIC_SURFACE precedent already
/// established on <c>Privon.Windows.ClipboardTextSnapshot</c> (Phase 3A.4 STEP3.2). No text
/// length, hash, or any other content-derived value is included either -- the safest contract is
/// no content-derived metadata at all.
/// </summary>
internal sealed class ClipboardWritePlan
{
    public ClipboardWritePlan(string replacementText)
    {
        ArgumentNullException.ThrowIfNull(replacementText);
        ReplacementText = replacementText;
    }

    public string ReplacementText { get; }

    public override string ToString() => $"{nameof(ClipboardWritePlan)} {{ }}";
}
