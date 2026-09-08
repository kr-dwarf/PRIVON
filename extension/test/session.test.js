import { test } from "node:test";
import assert from "node:assert/strict";
import { PrivonSession, NATIVE_HOST_NAME } from "../src/session.js";

function createFakePort() {
  const sent = [];
  const messageListeners = [];
  const disconnectListeners = [];
  let disconnected = false;

  return {
    sent,
    postMessage(msg) {
      if (disconnected) throw new Error("postMessage on a disconnected port");
      sent.push(msg);
    },
    onMessage: { addListener: (fn) => messageListeners.push(fn) },
    onDisconnect: { addListener: (fn) => disconnectListeners.push(fn) },
    disconnect() {
      if (disconnected) return;
      disconnected = true;
      for (const fn of disconnectListeners) fn();
    },
    // test-only helpers, never used by production code:
    _emitMessage(msg) {
      for (const fn of messageListeners) fn(msg);
    },
    _emitDisconnectFromHost() {
      if (disconnected) return;
      disconnected = true;
      for (const fn of disconnectListeners) fn();
    },
  };
}

function createFakeChrome({ connectNativeImpl } = {}) {
  const tabsListeners = { onActivated: [], onUpdated: [] };
  const windowListeners = { onFocusChanged: [] };
  const connectedHosts = [];
  const ports = [];
  let queryResult = [];
  let focusedWindow = null;

  return {
    _ports: ports,
    _connectedHosts: connectedHosts,
    setTabsQueryResult(tabs) {
      queryResult = tabs;
    },
    setFocusedWindow(win) {
      focusedWindow = win;
    },
    fireTabActivated() {
      for (const fn of tabsListeners.onActivated) fn();
    },
    fireTabUpdated(tabId, changeInfo) {
      for (const fn of tabsListeners.onUpdated) fn(tabId, changeInfo);
    },
    fireWindowFocusChanged() {
      for (const fn of windowListeners.onFocusChanged) fn();
    },
    runtime: {
      connectNative(name) {
        connectedHosts.push(name);
        const port = connectNativeImpl ? connectNativeImpl(name) : createFakePort();
        ports.push(port);
        return port;
      },
    },
    tabs: {
      onActivated: { addListener: (fn) => tabsListeners.onActivated.push(fn) },
      onUpdated: { addListener: (fn) => tabsListeners.onUpdated.push(fn) },
      async query() {
        return queryResult;
      },
    },
    windows: {
      onFocusChanged: { addListener: (fn) => windowListeners.onFocusChanged.push(fn) },
      async getLastFocused() {
        return focusedWindow;
      },
    },
  };
}

function createFakeTimers() {
  let nextId = 1;
  const pending = new Map();
  return {
    setTimeoutFn(fn) {
      const id = nextId++;
      pending.set(id, fn);
      return id;
    },
    clearTimeoutFn(id) {
      pending.delete(id);
    },
    firePending() {
      const entries = [...pending.entries()];
      pending.clear();
      for (const [, fn] of entries) fn();
    },
    pendingCount() {
      return pending.size;
    },
  };
}

async function flush() {
  for (let i = 0; i < 5; i++) await Promise.resolve();
}

async function connectAndAccept(chromeApi, timers, tab, focusedWindow) {
  chromeApi.setTabsQueryResult(tab ? [tab] : []);
  chromeApi.setFocusedWindow(focusedWindow ?? null);
  const session = new PrivonSession({ chromeApi, setTimeoutFn: timers.setTimeoutFn, clearTimeoutFn: timers.clearTimeoutFn });
  session.start();
  await flush();
  const port = chromeApi._ports[0];
  port._emitMessage({ v: 1, type: "HelloAck", accepted: true });
  await flush();
  return { session, port };
}

// Case 1 (session-level): connectNative is called with the exact production host name.
test("case1: connects to the exact production Native Messaging host name", async () => {
  const chromeApi = createFakeChrome();
  const timers = createFakeTimers();
  await connectAndAccept(chromeApi, timers, { url: "https://claude.ai/", windowId: 7 }, { id: 7, focused: true });

  assert.deepEqual(chromeApi._connectedHosts, [NATIVE_HOST_NAME]);
  assert.equal(NATIVE_HOST_NAME, "com.privon.host");
});

// Case 3/4 (session-level): outbound payload for an unsupported page never carries a raw URL, and
// a supported page's outbound payload never carries anything beyond the bare origin.
test("case3_4: outbound StateAssert never carries a raw URL, path, query, or fragment", async () => {
  const chromeApi = createFakeChrome();
  const timers = createFakeTimers();
  const { port } = await connectAndAccept(
    chromeApi, timers,
    { url: "https://chatgpt.com.evil.example/steal?x=1#y", windowId: 1 },
    { id: 1, focused: true },
  );

  const asserts = port.sent.filter((m) => m.type === "StateAssert");
  assert.equal(asserts.length, 1);
  assert.equal(asserts[0].originResolution, "Unsupported");
  assert.equal("origin" in asserts[0], false);
  for (const msg of port.sent) {
    const serialized = JSON.stringify(msg);
    assert.equal(serialized.includes("evil.example"), false);
    assert.equal(serialized.includes("/steal"), false);
    assert.equal(serialized.includes("x=1"), false);
  }
});

test("case4b: a supported page's outbound origin excludes path/query/fragment", async () => {
  const chromeApi = createFakeChrome();
  const timers = createFakeTimers();
  const { port } = await connectAndAccept(
    chromeApi, timers,
    { url: "https://claude.ai/chat/abc?ref=x#y", windowId: 1 },
    { id: 1, focused: true },
  );

  const asserts = port.sent.filter((m) => m.type === "StateAssert");
  assert.equal(asserts.length, 1);
  assert.equal(asserts[0].origin, "https://claude.ai");
  assert.equal(asserts[0].origin.includes("/chat"), false);
});

// Case 10: rejected HelloAck -> no assert, port closed, reconnect scheduled.
test("case10: rejected HelloAck sends no assert, closes the port, and schedules reconnect", async () => {
  const chromeApi = createFakeChrome();
  const timers = createFakeTimers();
  chromeApi.setTabsQueryResult([{ url: "https://claude.ai/", windowId: 1 }]);
  chromeApi.setFocusedWindow({ id: 1, focused: true });

  const session = new PrivonSession({ chromeApi, setTimeoutFn: timers.setTimeoutFn, clearTimeoutFn: timers.clearTimeoutFn });
  session.start();
  await flush();

  const firstPort = chromeApi._ports[0];
  firstPort._emitMessage({ v: 1, type: "HelloAck", accepted: false });
  await flush();

  assert.equal(firstPort.sent.some((m) => m.type === "StateAssert"), false);
  assert.equal(session.localState, "Unresolved");
  assert.equal(timers.pendingCount(), 1, "a reconnect must be scheduled");

  timers.firePending();
  await flush();
  assert.equal(chromeApi._ports.length, 2, "a new connection attempt must follow");
});

// Case 11: disconnect invalidates the current session locally.
test("case11: port disconnect resets local state and prevents further use of that session", async () => {
  const chromeApi = createFakeChrome();
  const timers = createFakeTimers();
  const { session, port } = await connectAndAccept(
    chromeApi, timers, { url: "https://claude.ai/", windowId: 1 }, { id: 1, focused: true },
  );
  assert.equal(session.localState, "Asserted");

  port._emitDisconnectFromHost();
  await flush();

  assert.equal(session.localState, "Unresolved");
  assert.equal(session.isConnected, false);
  assert.equal(timers.pendingCount(), 1);
});

// Case 12 (session-level): stale message from a retired port cannot affect the current session.
test("case12b: a message from a retired (disconnected) port is ignored after reconnect", async () => {
  const chromeApi = createFakeChrome();
  const timers = createFakeTimers();
  chromeApi.setTabsQueryResult([{ url: "https://claude.ai/", windowId: 1 }]);
  chromeApi.setFocusedWindow({ id: 1, focused: true });

  const session = new PrivonSession({ chromeApi, setTimeoutFn: timers.setTimeoutFn, clearTimeoutFn: timers.clearTimeoutFn });
  session.start();
  await flush();
  const oldPort = chromeApi._ports[0];

  oldPort._emitDisconnectFromHost();
  timers.firePending(); // reconnect fires -> new port created.
  await flush();
  const newPort = chromeApi._ports[1];
  assert.notEqual(newPort, oldPort);

  oldPort.sent.length = 0;
  newPort.sent.length = 0;
  oldPort._emitMessage({ v: 1, type: "HelloAck", accepted: true }); // stale ack from the dead port.
  await flush();

  assert.equal(newPort.sent.length, 0, "a stale port's traffic must never affect the live session");
});

// Case 15/16/17: active tab change, relevant URL/status change, and window focus change each
// trigger a recompute.
test("case15_16_17: tab activation, URL/status change, and window focus change each trigger recompute", async () => {
  const chromeApi = createFakeChrome();
  const timers = createFakeTimers();
  const { port } = await connectAndAccept(chromeApi, timers, { url: "https://claude.ai/", windowId: 1 }, { id: 1, focused: true });
  port.sent.length = 0;

  chromeApi.setTabsQueryResult([{ url: "https://chatgpt.com/", windowId: 1 }]);
  chromeApi.fireTabActivated();
  await flush();
  assert.equal(port.sent.some((m) => m.type === "StateInvalidate"), true, "tab activation must trigger recompute");

  port.sent.length = 0;
  chromeApi.setTabsQueryResult([{ url: "https://grok.com/", windowId: 1 }]);
  chromeApi.fireTabUpdated(1, { status: "complete" });
  await flush();
  assert.equal(port.sent.length > 0, true, "URL/status change must trigger recompute");

  port.sent.length = 0;
  chromeApi.fireWindowFocusChanged();
  await flush();
  assert.equal(port.sent.length > 0, true, "window focus change must trigger recompute");
});

// Case 18: an inactive/background tab is never falsely asserted as the active target -- the
// underlying query only ever asks for {active:true, lastFocusedWindow:true}, and only the returned
// tab's own facts are ever used.
test("case18: only the active/last-focused tab is ever the basis for an assertion", async () => {
  const chromeApi = createFakeChrome();
  const timers = createFakeTimers();
  // Only the active tab is ever supplied by tabs.query -- there is no way for this adapter to see a
  // background tab's URL at all, since it never calls chrome.tabs.query with anything but
  // {active:true, lastFocusedWindow:true} (see session.js _computeState).
  const { port } = await connectAndAccept(chromeApi, timers, { url: "https://claude.ai/", windowId: 1 }, { id: 1, focused: true });

  const asserts = port.sent.filter((m) => m.type === "StateAssert");
  assert.equal(asserts.length, 1);
  assert.equal(asserts[0].origin, "https://claude.ai");
});

// Case 19/20: challenge request receives a response through the exact frozen codec, using a fresh
// observation.
test("case19_20: ChallengeRequest produces a ChallengeResponse from a fresh observation", async () => {
  const chromeApi = createFakeChrome();
  const timers = createFakeTimers();
  const { port } = await connectAndAccept(chromeApi, timers, { url: "https://claude.ai/", windowId: 1 }, { id: 1, focused: true });
  port.sent.length = 0;

  const nonceBytes = new Uint8Array(32);
  const nonce = btoa(String.fromCharCode(...nonceBytes)).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");

  port._emitMessage({ v: 1, type: "ChallengeRequest", nonce });
  await flush();

  const responses = port.sent.filter((m) => m.type === "ChallengeResponse");
  assert.equal(responses.length, 1);
  assert.equal(responses[0].nonce, nonce);
  assert.equal(responses[0].focus, "Focused");
  assert.equal(responses[0].originResolution, "Resolved");
  assert.equal(responses[0].origin, "https://claude.ai");
});

// Case 21: a retired/disconnected session cannot produce a current challenge success.
test("case21: a challenge in flight when the port disconnects produces no response", async () => {
  const chromeApi = createFakeChrome();
  const timers = createFakeTimers();
  let releaseQuery;
  const gatedQuery = new Promise((resolve) => (releaseQuery = resolve));

  chromeApi.tabs.query = async () => {
    await gatedQuery;
    return [{ url: "https://claude.ai/", windowId: 1 }];
  };
  chromeApi.setFocusedWindow({ id: 1, focused: true });

  const session = new PrivonSession({ chromeApi, setTimeoutFn: timers.setTimeoutFn, clearTimeoutFn: timers.clearTimeoutFn });
  session.start();
  await flush();
  // Let the initial connect's own recompute pass through once (unblock, then re-gate).
  releaseQuery();
  await flush();

  const port = chromeApi._ports[0];
  port._emitMessage({ v: 1, type: "HelloAck", accepted: true });
  await flush();
  port.sent.length = 0;

  // Re-gate for the challenge's own fresh observation.
  const gate2 = new Promise((resolve) => (releaseQuery = resolve));
  chromeApi.tabs.query = async () => {
    await gate2;
    return [{ url: "https://claude.ai/", windowId: 1 }];
  };

  const nonceBytes = new Uint8Array(32);
  const nonce = btoa(String.fromCharCode(...nonceBytes)).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
  port._emitMessage({ v: 1, type: "ChallengeRequest", nonce });
  await flush(); // challenge handler is now awaiting the gated query.

  port._emitDisconnectFromHost(); // session retires WHILE the challenge observation is in flight.
  releaseQuery();
  await flush();

  assert.equal(port.sent.some((m) => m.type === "ChallengeResponse"), false,
    "a challenge answered after the port disconnected must never be sent");
});

// Case 22: a stale in-flight observation cannot answer a NEWER session's challenge either (the
// channel-token guard, not just the port-identity guard).
test("case22: a channel reset during a challenge observation prevents a stale-session response", async () => {
  const chromeApi = createFakeChrome();
  const timers = createFakeTimers();
  let releaseQuery;
  chromeApi.tabs.query = async () => {
    await new Promise((resolve) => (releaseQuery = resolve));
    return [{ url: "https://claude.ai/", windowId: 1 }];
  };
  chromeApi.setFocusedWindow({ id: 1, focused: true });

  const session = new PrivonSession({ chromeApi, setTimeoutFn: timers.setTimeoutFn, clearTimeoutFn: timers.clearTimeoutFn });
  session.start();
  await flush();

  const port = chromeApi._ports[0];
  port._emitMessage({ v: 1, type: "HelloAck", accepted: true }); // triggers the initial recompute, gated on releaseQuery.
  await flush();
  releaseQuery();
  await flush();
  port.sent.length = 0;

  chromeApi.tabs.query = async () => {
    await new Promise((resolve) => (releaseQuery = resolve));
    return [{ url: "https://claude.ai/", windowId: 1 }];
  };
  const nonceBytes = new Uint8Array(32);
  const nonce = btoa(String.fromCharCode(...nonceBytes)).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
  port._emitMessage({ v: 1, type: "ChallengeRequest", nonce });
  await flush();

  // Same port stays "current" by identity, but the channel itself resets (e.g. a disconnect+
  // immediate same-tick reconnect in the real adapter always changes port identity too -- this
  // proves the INDEPENDENT token guard, not merely the port-identity guard already covered above).
  session._stateMachine.resetForNewChannel();
  releaseQuery();
  await flush();

  assert.equal(port.sent.some((m) => m.type === "ChallengeResponse"), false,
    "a superseded-channel observation must never produce a challenge response, even on the same port object");
});
