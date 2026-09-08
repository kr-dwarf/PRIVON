// PRIVON MV3 extension -- the pure, browser-API-agnostic LOCAL state machine (Unresolved/Asserted)
// governing when StateInvalidate/StateAssert are sent over the Native Messaging channel. Contains
// NO chrome.* API calls, NO Native Messaging port handling, and NO origin/focus computation of its
// own -- those are injected as computeState/sendInvalidate/sendAssert, so this module can be driven
// entirely by deterministic fake functions in tests (no real browser, no real timers, no page
// content of any kind ever passes through here).
//
// FROZEN RULES (Gate E5E):
//   A) Unresolved + valid recompute -> StateAssert directly (no invalidate first).
//   B) Asserted + recompute needed -> StateInvalidate first, THEN recompute, THEN (only if the
//      result is still valid/current) StateAssert.
//   C) Unresolved + recompute -> recompute directly (same as A; no redundant invalidate).
//   D) A new channel/reconnect resets local state to Unresolved -- a prior channel's Asserted state
//      is never carried into a new ChannelId (see resetForNewChannel).
//   E) Recompute is single-flight: while one is running, duplicate triggers never start a second,
//      concurrent recompute -- they request exactly one COALESCED rerun once the current recompute
//      finishes (never an unbounded queue).
//   STALE_DISCARD: if resetForNewChannel() is called while a recompute from the PREVIOUS channel is
//   still in flight, that recompute's result -- however it turns out -- is discarded at its next
//   yield point; it can never assert into the new channel.

export class LocalStateMachine {
  /**
   * @param {object} deps
   * @param {() => Promise<{focus:string, originResolution:string, origin:(string|null)} | null>} deps.computeState
   *   Returns null when no valid observation could be made at all -- the recompute is then
   *   discarded (local state stays/returns to Unresolved, no StateAssert is sent).
   * @param {() => (Promise<void>|void)} deps.sendInvalidate
   * @param {(focus:string, originResolution:string, origin:(string|null)) => (Promise<void>|void)} deps.sendAssert
   */
  constructor({ computeState, sendInvalidate, sendAssert }) {
    this._computeState = computeState;
    this._sendInvalidate = sendInvalidate;
    this._sendAssert = sendAssert;

    this._localState = "Unresolved";
    this._channelToken = 0;
    this._inFlight = false;
    this._rerunRequested = false;
  }

  get localState() {
    return this._localState;
  }

  /** Opaque token that changes on every resetForNewChannel() call -- exposed so a caller (e.g. a
   * native-challenge handler) can independently detect the same staleness condition this class
   * itself guards against. */
  get channelToken() {
    return this._channelToken;
  }

  /** D) New channel/reconnect: bump the staleness token and reset local state. An in-flight
   * recompute from the previous token is never force-cancelled -- it is simply left to discard
   * itself via the token check the next time it reaches a yield point (see STALE_DISCARD). */
  resetForNewChannel() {
    this._channelToken += 1;
    this._localState = "Unresolved";
    this._rerunRequested = false;
  }

  /** E) Single-flight entry point for every recompute trigger (tab change, focus change, initial
   * connect, etc.). */
  requestRecompute() {
    if (this._inFlight) {
      this._rerunRequested = true;
      return;
    }

    this._inFlight = true;
    void this._runRecompute();
  }

  async _runRecompute() {
    const token = this._channelToken;

    try {
      if (this._localState === "Asserted") {
        await this._sendInvalidate();
        if (token !== this._channelToken) return; // STALE_DISCARD: superseded during invalidate.
        this._localState = "Unresolved";
      }

      const result = await this._computeState();

      if (token !== this._channelToken) return; // STALE_DISCARD: superseded during recompute.
      if (result === null) return; // no valid observation -- stay Unresolved, no assert.

      await this._sendAssert(result.focus, result.originResolution, result.origin ?? null);
      if (token !== this._channelToken) return; // superseded while the assert send was in flight.

      this._localState = "Asserted";
    } finally {
      this._inFlight = false;
      if (this._rerunRequested) {
        this._rerunRequested = false;
        this.requestRecompute(); // exactly one coalesced rerun -- never a queue.
      }
    }
  }
}
