using Privon.Windows;

namespace Privon.App;

/// <summary>
/// Phase 3C STEP31/31.1 -- the single pending composer-verification attempt
/// <see cref="ClipboardComposerVerifier"/> owns, bound to exactly one verified protected clipboard
/// write. A plain <c>sealed class</c>, not a record -- REFERENCE identity (not value equality) is
/// what <see cref="ClipboardComposerVerifier"/>'s compare-and-clear relies on, exactly mirroring
/// <c>ClipboardDecisionScope</c>'s own established "plain sealed class, so two otherwise-identical
/// instances are never treated as interchangeable" precedent.
///
/// <see cref="ExpectedGeneration"/> is captured ONCE, at construction (verified-write handoff
/// time) -- immutable for this record's lifetime, NEVER rebound to a later
/// <see cref="IClipboardGenerationSnapshot.CurrentGeneration"/> read (Phase 3C STEP30.2's
/// PENDING_VERIFICATION_GENERATION_REBIND -&gt; FORBIDDEN invariant).
///
/// <see cref="ExpectedProtectedText"/> is policy-processed user content whose Protect-selected
/// spans were replaced -- it is NOT guaranteed PII-free (base-policy Bypass values, undetected
/// PII, and ordinary sensitive/non-PII content may all still be present verbatim -- Phase 3C
/// STEP30.1's EXPECTED_PROTECTED_TEXT_CLASSIFICATION). No raw pre-rewrite clipboard text,
/// <c>AliasMap</c>, <c>AliasAssignment</c>, <c>CanonicalValue</c>, <c>RevisionStamp</c>,
/// <c>SnapshotHash</c>, <c>ClipboardWritePlan</c>, <c>ClipboardTextSnapshot</c>, or composer text
/// is ever carried here -- exactly these three fields, nothing else.
///
/// PENDING_DIAGNOSTIC_SURFACE: <see cref="ToString"/> exposes <see cref="ExpectedGeneration"/>
/// only (already-safe metadata, matching <c>Privon.Core.RevisionId</c>'s classification) -- never
/// <see cref="ExpectedProtectedText"/> (not even a length/preview/hash) and, per Phase 3C
/// STEP31.1's "keep surface minimal" instruction, not even <see cref="ExpectedTarget"/>.
/// </summary>
internal sealed class PendingComposerVerification
{
    public PendingComposerVerification(
        ForegroundTargetSnapshot expectedTarget, string expectedProtectedText, long expectedGeneration)
    {
        ArgumentNullException.ThrowIfNull(expectedProtectedText);
        ExpectedTarget = expectedTarget;
        ExpectedProtectedText = expectedProtectedText;
        ExpectedGeneration = expectedGeneration;
    }

    public ForegroundTargetSnapshot ExpectedTarget { get; }
    public string ExpectedProtectedText { get; }
    public long ExpectedGeneration { get; }

    public override string ToString() =>
        $"{nameof(PendingComposerVerification)} {{ {nameof(ExpectedGeneration)} = {ExpectedGeneration} }}";
}
