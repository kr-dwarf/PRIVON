using Privon.Browser;
using Privon.Core;

namespace Privon.App;

/// <summary>
/// PRIVON 0.3.1 Gate 031C (frozen formula, terms C/F/G/H) / Gate 031E1 (this type) / Gate 031F3
/// (bound-proof correction) -- the explicit, caller-supplied decision-time facts
/// <see cref="WebTargetGate.Match"/> compares a <see cref="WebForegroundEvidence"/> instance
/// against. Deliberately a SEPARATE type from <see cref="WebForegroundEvidence"/>, not a widening
/// of it (Gate 031C section 10: "prefer a separate decision context over adding a new field to
/// WebForegroundEvidence unless necessary" -- the frozen seven-field evidence model stays exactly
/// seven fields).
///
/// NO_FAKE_TRANSPORT (Gate 031C section 11, load-bearing): every field here is an EXTERNALLY
/// PROVEN fact this type's own production code never manufactures. In particular,
/// <see cref="ChallengeProof"/> is consumed, never performed, here -- Gate 031F3 implements no
/// Native Messaging channel, no decision-time challenge/response transport, and no bracketing
/// foreground re-capture of any kind. A caller (today: only this gate's own test suite; in a
/// future mechanics gate: the real channel/challenge subsystem) is solely responsible for
/// constructing a genuine <see cref="WebChallengeProof"/> only after a real bracketed exchange
/// succeeds. <see cref="WebTargetGate.Match"/> only ever requires and compares it, and rejects
/// when it is absent or does not agree with evidence/current state on all four of its fields -- it
/// never claims to have established it, and no production helper anywhere in this codebase
/// manufactures a trusted proof out of nothing.
///
/// BOUND_PROOF_REPLACES_BOOLEAN (Gate 031F3, correcting Gate 031E1's original
/// <c>ChallengeConfirmed</c> bool, now removed entirely -- zero remaining production or test
/// references, no compatibility overload, no obsolete alias): a bare bool could not express WHAT
/// state was confirmed and was not re-checkable at guarded-transport CHECK1/CHECK2, because there
/// was nothing to compare it against. <see cref="ChallengeProof"/> fixes both problems by being
/// bound, field-for-field, to the exact same channel/revision/epoch/process facts
/// <see cref="WebForegroundEvidence"/> and this type's own current-state fields already carry.
///
/// <see cref="CurrentRevision"/>/<see cref="CurrentChannelId"/>/<see cref="CurrentForegroundEpoch"/>
/// are what a genuinely live channel/revision/epoch tracker would report "right now" -- comparing
/// an evidence instance's own fields against these (rather than against some fixed/hardcoded
/// value) is what makes staleness meaningful; Gate 031F3 does not implement that live tracker
/// either, so today's only producer of these values is this gate's own test suite. Deliberately no
/// <c>CurrentBrowserProcessId</c> field: the current PID authority remains exclusively
/// <c>ForegroundTargetSnapshot.ProcessId</c> (Gate 031F3 section 3) -- duplicating it here would
/// only invite the two to silently drift apart.
/// </summary>
internal readonly record struct WebDecisionContext(
    RevisionId CurrentRevision,
    long CurrentChannelId,
    long CurrentForegroundEpoch,
    WebChallengeProof? ChallengeProof);
