// PRIVON MV3 extension -- thin adapter binding chrome.* browser APIs and the Native Messaging port
// to the pure LocalStateMachine (stateMachine.js) and protocol codec (protocol.js). Owns: the
// Hello/HelloAck handshake, port disconnect handling, bounded deterministic reconnect/backoff,
// chrome.tabs/chrome.windows event wiring (recompute triggers only -- never DOM/page content), and
// native ChallengeRequest -> ChallengeResponse handling using a FRESH state observation for the
// CURRENT session (never a stale cached result).
//
// PRIVACY BOUNDARY (Gate E5E B1/B2): this file never references document, innerText, innerHTML,
// chrome.scripting, chrome.webRequest, or chrome.debugger, and never reads clipboard/composer/page
// content of any kind. The only browser facts consulted are: active-tab URL (immediately reduced to
// an origin-or-Unsupported classification via origin.js and NEVER retained beyond that), tab
// window id, and window focus state.
//
// HOST_NAME: "com.privon.host" -- the production Native Messaging host, never the E5B dev-only
// "com.privon.devmeasure". No production Chrome/Edge Web Store extension ID is referenced anywhere
// in this file (Gate E5E: store identity acquisition is E5G's job, not this gate's).

import { FOCUS, buildHello, buildStateInvalidate, buildStateAssert, buildChallengeResponse, parseHelloAck, parseChallengeRequest } from "./protocol.js";
import { classifyOrigin } from "./origin.js";
import { LocalStateMachine } from "./stateMachine.js";

export const NATIVE_HOST_NAME = "com.privon.host";

// Bounded, deterministic backoff ladder (Gate E5E: "do not create a tight reconnect loop... do not
// freeze arbitrary production timing constants unless needed for correctness" -- these are a
// reasonable, non-frozen starting ladder, easily retuned later without touching the mechanics).
const RECONNECT_DELAYS_MS = Object.freeze([1000, 2000, 5000, 10000, 30000]);

export class PrivonSession {
  /**
   * @param {object} deps
   * @param {object} deps.chromeApi - the exact chrome.* surface used:
   *   runtime.connectNative(name), tabs.onActivated/onUpdated.addListener, tabs.query(query),
   *   windows.onFocusChanged.addListener, windows.getLastFocused(options).
   * @param {(fn:()=>void, ms:number) => any} [deps.setTimeoutFn]
   * @param {(handle:any) => void} [deps.clearTimeoutFn]
   */
  constructor({ chromeApi, setTimeoutFn = setTimeout, clearTimeoutFn = clearTimeout }) {
    this._chrome = chromeApi;
    this._setTimeout = setTimeoutFn;
    this._clearTimeout = clearTimeoutFn;

    this._port = null;
    this._handshakeAccepted = false;
    this._reconnectAttempt = 0;
    this._reconnectHandle = null;
    this._stopped = true;

    this._stateMachine = new LocalStateMachine({
      computeState: () => this._computeState(),
      sendInvalidate: () => this._sendToPort(buildStateInvalidate()),
      sendAssert: (focus, originResolution, origin) => this._sendToPort(buildStateAssert(focus, originResolution, origin)),
    });

    this._onTabActivated = () => this._stateMachine.requestRecompute();
    this._onTabUpdated = (_tabId, changeInfo) => {
      // Recompute only on a mechanically relevant change -- never on every property tick.
      if (changeInfo.url !== undefined || changeInfo.status === "complete") {
        this._stateMachine.requestRecompute();
      }
    };
    this._onWindowFocusChanged = () => this._stateMachine.requestRecompute();
  }

  get localState() {
    return this._stateMachine.localState;
  }

  get isConnected() {
    return this._port !== null && this._handshakeAccepted;
  }

  start() {
    if (!this._stopped) return;
    this._stopped = false;
    this._chrome.tabs.onActivated.addListener(this._onTabActivated);
    this._chrome.tabs.onUpdated.addListener(this._onTabUpdated);
    this._chrome.windows.onFocusChanged.addListener(this._onWindowFocusChanged);
    this._connect();
  }

  stop() {
    this._stopped = true;
    if (this._reconnectHandle !== null) {
      this._clearTimeout(this._reconnectHandle);
      this._reconnectHandle = null;
    }
    this._disconnectPort();
  }

  _connect() {
    if (this._stopped) return;

    this._handshakeAccepted = false;
    this._stateMachine.resetForNewChannel(); // D) new channel resets local state to Unresolved.

    let port;
    try {
      port = this._chrome.runtime.connectNative(NATIVE_HOST_NAME);
    } catch {
      this._scheduleReconnect();
      return;
    }

    this._port = port;
    port.onMessage.addListener((message) => this._onPortMessage(port, message));
    port.onDisconnect.addListener(() => this._onPortDisconnect(port));

    this._sendToPort(buildHello(), port);
  }

  _onPortMessage(port, message) {
    if (port !== this._port) return; // message from a retired port -- ignore.

    if (!this._handshakeAccepted) {
      const ack = parseHelloAck(message);
      if (ack === null) return; // malformed pre-handshake traffic -- ignore, never treated as accepted.

      if (!ack.accepted) {
        // Rejected HelloAck: never assert into this session, close it, and reconnect.
        this._disconnectPort();
        this._scheduleReconnect();
        return;
      }

      this._handshakeAccepted = true;
      this._reconnectAttempt = 0;
      this._stateMachine.requestRecompute(); // A) fresh Unresolved -> compute -> assert directly.
      return;
    }

    const challenge = parseChallengeRequest(message);
    if (challenge !== null) {
      void this._handleChallenge(port, challenge.nonce);
    }
    // Any other post-handshake message shape is ignored -- the frozen protocol defines no other
    // extension-inbound message type.
  }

  async _handleChallenge(port, nonce) {
    // FRESH observation for the CURRENT session -- never answered from a cached/stale result.
    const observedToken = this._stateMachine.channelToken;
    const result = await this._computeState();

    if (port !== this._port) return; // session retired while observing -- do not answer.
    if (observedToken !== this._stateMachine.channelToken) return; // superseded -- do not answer.
    if (result === null) return; // no valid observation available -- do not fabricate a response.

    const response = buildChallengeResponse(nonce, result.focus, result.originResolution, result.origin);
    this._sendToPort(response, port);
  }

  _onPortDisconnect(port) {
    if (port !== this._port) return;

    this._port = null;
    this._handshakeAccepted = false;
    // Disconnect is authoritative: bumping the channel token means any recompute/challenge still in
    // flight from this session can never assert/answer, and local state resets to Unresolved.
    this._stateMachine.resetForNewChannel();

    if (!this._stopped) this._scheduleReconnect();
  }

  _disconnectPort() {
    if (this._port !== null) {
      try {
        this._port.disconnect();
      } catch {
        // best-effort only.
      }
    }
    this._port = null;
    this._handshakeAccepted = false;
  }

  _scheduleReconnect() {
    if (this._stopped || this._reconnectHandle !== null) return;

    const delay = RECONNECT_DELAYS_MS[Math.min(this._reconnectAttempt, RECONNECT_DELAYS_MS.length - 1)];
    this._reconnectAttempt += 1;
    this._reconnectHandle = this._setTimeout(() => {
      this._reconnectHandle = null;
      this._connect();
    }, delay);
  }

  _sendToPort(message, port = this._port) {
    if (port === null) return;
    try {
      port.postMessage(message);
    } catch {
      // best-effort -- a failed post generally means the port is already dead; the real onDisconnect
      // event (not simulated here) drives actual recovery.
    }
  }

  /** The ONLY place browser facts are observed. Reduces an active tab's URL to an origin
   * classification immediately (see origin.js) -- the raw URL itself never leaves this function. */
  async _computeState() {
    let tabs;
    try {
      tabs = await this._chrome.tabs.query({ active: true, lastFocusedWindow: true });
    } catch {
      return null;
    }

    if (!Array.isArray(tabs) || tabs.length === 0) {
      return null;
    }

    const tab = tabs[0];
    const { resolution, origin } = classifyOrigin(tab.url);

    let focus;
    if (tab.windowId === undefined) {
      focus = FOCUS.UNRESOLVED;
    } else {
      let focusedWindow;
      try {
        focusedWindow = await this._chrome.windows.getLastFocused({ populate: false });
      } catch {
        focusedWindow = null;
      }

      focus = focusedWindow && focusedWindow.focused && focusedWindow.id === tab.windowId
        ? FOCUS.FOCUSED
        : FOCUS.NOT_FOCUSED;
    }

    return { focus, originResolution: resolution, origin };
  }
}
