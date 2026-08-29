using Privon.Windows;

namespace Privon.App;

/// <summary>
/// Phase 3C STEP31/31.1/32 -- the only production implementation of
/// <see cref="IClipboardComposerVerificationHandoff"/>/<see cref="IClipboardComposerVerificationInvalidation"/>,
/// and the owner of <see cref="VerifyAsync"/>. Owns the single pending composer-verification slot
/// (<see cref="_pending"/>) -- <see cref="ClipboardDecisionScopeLifecycle"/> is never widened to
/// hold this state (Phase 3C STEP31 audit's PENDING_OWNER, frozen).
///
/// Dependencies exactly as frozen: <see cref="IClipboardGenerationSnapshot"/> (generation-freshness
/// only, never the wider <see cref="IClipboardDecisionScopeLifecycle"/>),
/// <see cref="IComposerReadTransport"/>, <see cref="IClipboardOperationGate"/> (the SAME concrete
/// instance <see cref="PrivonAppComposition.BuildGraph"/> gives <see cref="ClipboardPrivacyCoordinator"/>/
/// <c>ClipboardDecisionActionResolver"/> -- two independent instances would serialize nothing
/// relative to each other). NO <see cref="IForegroundTargetCapture"/> dependency of any kind --
/// the composer read always uses the pinned pending record's own
/// <see cref="PendingComposerVerification.ExpectedTarget"/>, never a fresh capture (Phase 3C
/// STEP31 audit's NO_FRESH_TARGET_CAPTURE).
///
/// PUBLISH (Phase 3C STEP31.1's corrected three-phase sequence): PRECHECK (reject before touching
/// the slot if the generation has already moved on) -&gt; INSTALL (unconditional write under
/// <see cref="_pendingGate"/> -- safe because <see cref="Publish"/> is only ever called from
/// inside an attempt already holding <see cref="_operationGate"/>, so no second concurrent
/// <see cref="Publish"/> call can ever be in flight) -&gt; POST_INSTALL_VALIDATION (a SECOND,
/// independent generation re-read; a mismatch here means a notification landed in the brief
/// PRECHECK-to-INSTALL/INSTALL-to-here window, so the just-installed record is immediately
/// identity-cleared rather than left resident -- STALE_PENDING_REINTRODUCTION_AFTER_NOTIFICATION
/// -&gt; FORBIDDEN). Everything AFTER a successful POST_INSTALL_VALIDATION is covered by the
/// ordinary, already-eager <see cref="InvalidatePending"/> path -- no third check is needed.
///
/// INVALIDATE_PENDING: unconditional, synchronous, bounded (a lock plus a null-assignment) --
/// called by <see cref="ClipboardPrivacyCoordinator.OnClipboardChanged"/> for EVERY clipboard
/// notification, text or not, mirroring <see cref="ClipboardDecisionScopeLifecycle.AdvanceOnClipboardNotification"/>'s
/// own unconditional <c>_activeScope = null</c> precedent applied to this independently-owned
/// state. Never touches <see cref="_operationGate"/>, never performs UI Automation, never inspects
/// the discarded record's content.
///
/// VERIFYASYNC gate order (Phase 3C STEP31 audit's SHARED_GATE_ORDER, frozen): acquire
/// <see cref="_operationGate"/> BEFORE even checking whether a pending record exists (uniform
/// acquisition boundary, matching <see cref="ClipboardPrivacyCoordinator.ProcessNotificationAsync"/>'s
/// own precedent of holding the gate through fast rejections) -&gt; pin the pending record ONCE
/// under <see cref="_pendingGate"/> (PINNED_ATTEMPT_IDENTITY: every subsequent step uses only that
/// pinned local reference, never re-reading <see cref="_pending"/>) -&gt; pre-read generation check
/// (stale -&gt; identity-clear the PINNED record, <see cref="VerificationOutcome.Stale"/>, no
/// composer read at all) -&gt; composer read via <see cref="_composerReadTransport"/> using the
/// pinned record's own <see cref="PendingComposerVerification.ExpectedTarget"/> exactly -&gt;
/// post-read generation check (stale -&gt; identity-clear, <see cref="VerificationOutcome.Stale"/>
/// -- the composer read's own result, even a coincidental match, is discarded unread once the
/// generation is known stale) -&gt; outcome mapping (see <see cref="MapOutcome"/>) -&gt; terminal
/// identity-checked clear in every case -&gt; release the gate in <c>finally</c>.
///
/// COMPARE_AND_CLEAR (<see cref="TryClearPending"/>): reference-identity-checked (never value
/// equality, never a GUID) -- so a slow terminal cleanup from an attempt whose pinned record has
/// already been superseded by a genuinely newer <see cref="Publish"/> (or already discarded by
/// <see cref="InvalidatePending"/>) never clears that newer state.
///
/// ERROR_BOUNDARY: <see cref="VerifyAsync"/> has its own independent outer <c>try</c>/<c>catch</c>
/// -- never reuses <see cref="ClipboardPrivacyCoordinator.RunWorkerAsync"/>'s own catch, since this
/// is its own top-level entry point. An unexpected exception maps to
/// <see cref="VerificationOutcome.Failed"/>, is never logged with any content-bearing text, always
/// identity-clears the pinned record if one was pinned, and always releases the gate via
/// <c>finally</c>.
///
/// SENSITIVE_TEXT_LIFETIME: <see cref="PendingComposerVerification.ExpectedProtectedText"/> and
/// the composer text read during one attempt exist only as local variables/the pending record's own
/// field -- never a persistent <see cref="string"/> field on this type, never logged, never
/// included in any exception message. Only reference-discard/GC-eligibility is claimed, never
/// deterministic zeroization (same discipline as every other sensitive value in this codebase).
/// </summary>
internal sealed class ClipboardComposerVerifier : IClipboardComposerVerificationHandoff, IClipboardComposerVerificationInvalidation
{
    private readonly IClipboardGenerationSnapshot _generationSnapshot;
    private readonly IComposerReadTransport _composerReadTransport;
    private readonly IClipboardOperationGate _operationGate;

    private readonly object _pendingGate = new();
    private PendingComposerVerification? _pending;

    public ClipboardComposerVerifier(
        IClipboardGenerationSnapshot generationSnapshot,
        IComposerReadTransport composerReadTransport,
        IClipboardOperationGate operationGate)
    {
        ArgumentNullException.ThrowIfNull(generationSnapshot);
        ArgumentNullException.ThrowIfNull(composerReadTransport);
        ArgumentNullException.ThrowIfNull(operationGate);
        _generationSnapshot = generationSnapshot;
        _composerReadTransport = composerReadTransport;
        _operationGate = operationGate;
    }

    public bool Publish(ForegroundTargetSnapshot expectedTarget, string expectedProtectedText, long expectedGeneration)
    {
        // PRECHECK -- reject before the pending slot is touched at all if already known stale.
        if (_generationSnapshot.CurrentGeneration != expectedGeneration)
            return false;

        var newRecord = new PendingComposerVerification(expectedTarget, expectedProtectedText, expectedGeneration);

        // INSTALL -- unconditional; safe because Publish is only ever called from inside an
        // attempt already holding _operationGate, so no second Publish call can be concurrently
        // in flight (Phase 3C STEP31 audit's PUBLICATION_GENERATION_RACE proof).
        lock (_pendingGate)
        {
            _pending = newRecord;
        }

        // POST_INSTALL_VALIDATION (Phase 3C STEP31.1 correction) -- a notification may have
        // advanced the generation in the PRECHECK-to-INSTALL window; if so, the just-installed
        // record must not be left resident.
        if (_generationSnapshot.CurrentGeneration != expectedGeneration)
        {
            TryClearPending(newRecord);
            return false;
        }

        return true;
    }

    public void InvalidatePending()
    {
        lock (_pendingGate)
        {
            _pending = null;
        }
    }

    public async Task<ComposerVerificationResult> VerifyAsync()
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);

        PendingComposerVerification? pinned = null;
        try
        {
            lock (_pendingGate)
            {
                pinned = _pending;
            }

            if (pinned is null)
                return new ComposerVerificationResult(VerificationOutcome.NotAttempted);

            if (_generationSnapshot.CurrentGeneration != pinned.ExpectedGeneration)
            {
                TryClearPending(pinned);
                return new ComposerVerificationResult(VerificationOutcome.Stale);
            }

            var composerResult = await _composerReadTransport
                .ReadFocusedComposerTextAsync(pinned.ExpectedTarget)
                .ConfigureAwait(false);

            if (_generationSnapshot.CurrentGeneration != pinned.ExpectedGeneration)
            {
                TryClearPending(pinned);
                return new ComposerVerificationResult(VerificationOutcome.Stale);
            }

            var outcome = MapOutcome(composerResult, pinned);
            TryClearPending(pinned);
            return new ComposerVerificationResult(outcome);
        }
        catch
        {
            if (pinned is not null)
                TryClearPending(pinned);
            return new ComposerVerificationResult(VerificationOutcome.Failed);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    // OUTCOME_MAPPING (Phase 3C STEP31.1, frozen): explicit switch, never an enum numeric cast.
    // NotRunning/InvalidExpectedTarget -> Failed (an invariant-violation signal, not routine
    // drift -- pinned.ExpectedTarget originates from an already-authorized verified-write handoff,
    // so ComposerTextReader's own IsValidExpectedTarget check should be structurally impossible to
    // fail here; NotRunning means the transport itself is not operating as expected, a lifecycle
    // problem rather than a per-attempt environmental one). Every other non-Success outcome maps
    // 1:1 to the identically-named VerificationOutcome member.
    private static VerificationOutcome MapOutcome(ComposerTextReadResult composerResult, PendingComposerVerification pinned)
    {
        switch (composerResult.Outcome)
        {
            case ComposerReadOutcome.Success:
                // EXACT_TEXT: exact ordinal whole-string equality only -- no trim, no
                // normalization, no substring, no Contains. A Success result carrying a null Text
                // would violate ComposerTextReadResult's own documented contract -- fail closed by
                // throwing rather than silently treating null as empty (caught by VerifyAsync's
                // own outer catch -> Failed, matching ComposerTextReader.MapSnapshot's own
                // precedent of throwing on an impossible/undefined shape).
                if (composerResult.Text is not { } text)
                    throw new InvalidOperationException(
                        "ComposerTextReadResult.Success carried a null Text, violating its own contract.");

                return string.Equals(text, pinned.ExpectedProtectedText, StringComparison.Ordinal)
                    ? VerificationOutcome.Verified
                    : VerificationOutcome.Mismatch;

            case ComposerReadOutcome.NotRunning:
                return VerificationOutcome.Failed;

            case ComposerReadOutcome.InvalidExpectedTarget:
                return VerificationOutcome.Failed;

            case ComposerReadOutcome.TargetUnavailable:
                return VerificationOutcome.TargetUnavailable;

            case ComposerReadOutcome.TargetChanged:
                return VerificationOutcome.TargetChanged;

            case ComposerReadOutcome.ComposerNotFocused:
                return VerificationOutcome.ComposerNotFocused;

            case ComposerReadOutcome.TextUnavailable:
                return VerificationOutcome.TextUnavailable;

            case ComposerReadOutcome.AutomationFailure:
                return VerificationOutcome.AutomationFailure;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(composerResult), composerResult.Outcome, "Undefined ComposerReadOutcome value.");
        }
    }

    // COMPARE_AND_CLEAR: reference-identity-checked, never value equality, never a GUID.
    private bool TryClearPending(PendingComposerVerification expected)
    {
        lock (_pendingGate)
        {
            if (!ReferenceEquals(_pending, expected)) return false;
            _pending = null;
            return true;
        }
    }
}
