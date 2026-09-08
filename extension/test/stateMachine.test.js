import { test } from "node:test";
import assert from "node:assert/strict";
import { LocalStateMachine } from "../src/stateMachine.js";

function deferred() {
  let resolve;
  const promise = new Promise((r) => (resolve = r));
  return { promise, resolve };
}

function makeMachine({ results = [] } = {}) {
  const calls = [];
  let resultIndex = 0;
  const gates = []; // optional per-call deferred gates, set by individual tests.

  const machine = new LocalStateMachine({
    computeState: async () => {
      calls.push("compute"); // recorded at INVOCATION time, not completion -- required for case13's
      // single-flight assertion (a second concurrent invocation must never happen even while the
      // first is still blocked on its gate).
      const gate = gates[resultIndex];
      if (gate) await gate.promise;
      const result = results[resultIndex] ?? null;
      resultIndex += 1;
      return result;
    },
    sendInvalidate: async () => {
      calls.push("invalidate");
    },
    sendAssert: async (focus, originResolution, origin) => {
      calls.push({ type: "assert", focus, originResolution, origin });
    },
  });

  return { machine, calls, gates };
}

async function flushMicrotasks() {
  await Promise.resolve();
  await Promise.resolve();
  await Promise.resolve();
}

// Case 5: fresh Unresolved + valid recompute -> StateAssert directly, no invalidate.
test("case5: fresh Unresolved valid recompute asserts without a prior invalidate", async () => {
  const { machine, calls } = makeMachine({
    results: [{ focus: "Focused", originResolution: "Resolved", origin: "https://claude.ai" }],
  });

  assert.equal(machine.localState, "Unresolved");
  machine.requestRecompute();
  await flushMicrotasks();

  assert.deepEqual(calls, [
    "compute",
    { type: "assert", focus: "Focused", originResolution: "Resolved", origin: "https://claude.ai" },
  ]);
  assert.equal(machine.localState, "Asserted");
});

// Case 6: Asserted + recompute -> StateInvalidate THEN StateAssert, in that order.
test("case6: asserted recompute sends StateInvalidate then StateAssert in order", async () => {
  const { machine, calls } = makeMachine({
    results: [
      { focus: "Focused", originResolution: "Resolved", origin: "https://claude.ai" },
      { focus: "Focused", originResolution: "Resolved", origin: "https://chatgpt.com" },
    ],
  });

  machine.requestRecompute();
  await flushMicrotasks();
  assert.equal(machine.localState, "Asserted");
  calls.length = 0;

  machine.requestRecompute();
  await flushMicrotasks();

  assert.deepEqual(calls, [
    "invalidate",
    "compute",
    { type: "assert", focus: "Focused", originResolution: "Resolved", origin: "https://chatgpt.com" },
  ]);
  assert.equal(machine.localState, "Asserted");
});

// Case 7: Asserted -> unsupported recompute sends invalidate, and never a falsely-supported assert.
test("case7: asserted-to-unsupported recompute invalidates and never asserts a supported origin", async () => {
  const { machine, calls } = makeMachine({
    results: [
      { focus: "Focused", originResolution: "Resolved", origin: "https://claude.ai" },
      { focus: "Focused", originResolution: "Unsupported", origin: null },
    ],
  });

  machine.requestRecompute();
  await flushMicrotasks();
  calls.length = 0;

  machine.requestRecompute();
  await flushMicrotasks();

  assert.deepEqual(calls, [
    "invalidate",
    "compute",
    { type: "assert", focus: "Focused", originResolution: "Unsupported", origin: null },
  ]);
  for (const c of calls) {
    if (typeof c === "object") assert.notEqual(c.originResolution, "Resolved");
  }
});

// Case 8: Unresolved -> unsupported recompute never emits a redundant invalidate.
test("case8: unresolved-to-unsupported recompute emits no unnecessary invalidate", async () => {
  const { machine, calls } = makeMachine({
    results: [{ focus: "NotFocused", originResolution: "Unsupported", origin: null }],
  });

  assert.equal(machine.localState, "Unresolved");
  machine.requestRecompute();
  await flushMicrotasks();

  assert.equal(calls.includes("invalidate"), false);
  assert.deepEqual(calls, [
    "compute",
    { type: "assert", focus: "NotFocused", originResolution: "Unsupported", origin: null },
  ]);
});

// Case 9: new channel/reconnect resets local state to Unresolved.
test("case9: resetForNewChannel resets local state to Unresolved even from Asserted", async () => {
  const { machine } = makeMachine({
    results: [{ focus: "Focused", originResolution: "Resolved", origin: "https://claude.ai" }],
  });

  machine.requestRecompute();
  await flushMicrotasks();
  assert.equal(machine.localState, "Asserted");

  machine.resetForNewChannel();
  assert.equal(machine.localState, "Unresolved");
});

// Case 12: an old-session asynchronous recompute cannot assert into a new session.
test("case12: a recompute in flight when the channel resets can never assert", async () => {
  const gate = deferred();
  const { machine, calls, gates } = makeMachine({
    results: [{ focus: "Focused", originResolution: "Resolved", origin: "https://claude.ai" }],
  });
  gates[0] = gate;

  machine.requestRecompute(); // starts, blocks inside computeState on the gate.
  await flushMicrotasks();
  assert.deepEqual(calls, ["compute"]); // invoked, but still awaiting the gate -- no result yet.

  machine.resetForNewChannel(); // new channel arrives WHILE the old recompute is still in flight.
  gate.resolve(); // now let the stale recompute's computeState() finish.
  await flushMicrotasks();

  assert.equal(calls.some((c) => typeof c === "object" && c.type === "assert"), false,
    "a stale recompute from a superseded channel must never send StateAssert");
  assert.equal(machine.localState, "Unresolved");
});

// Case 13: duplicate events during an in-flight recompute do not run concurrently.
test("case13: duplicate triggers during an in-flight recompute do not start a second recompute", async () => {
  const gate = deferred();
  const { machine, calls, gates } = makeMachine({
    results: [
      { focus: "Focused", originResolution: "Resolved", origin: "https://claude.ai" },
      { focus: "Focused", originResolution: "Resolved", origin: "https://chatgpt.com" },
    ],
  });
  gates[0] = gate;

  machine.requestRecompute();
  await flushMicrotasks();
  machine.requestRecompute(); // duplicate while the first is still blocked on the gate.
  machine.requestRecompute(); // and again.
  await flushMicrotasks();

  const computeCallsSoFar = calls.filter((c) => c === "compute").length;
  assert.equal(computeCallsSoFar, 1, "duplicate triggers must not launch a concurrent second compute");

  gate.resolve();
  await flushMicrotasks();
});

// Case 14: duplicate events cause exactly one coalesced rerun after the current recompute finishes.
test("case14: duplicate triggers during in-flight recompute cause exactly one coalesced rerun", async () => {
  const gate = deferred();
  const { machine, calls, gates } = makeMachine({
    results: [
      { focus: "Focused", originResolution: "Resolved", origin: "https://claude.ai" },
      { focus: "Focused", originResolution: "Unsupported", origin: null },
    ],
  });
  gates[0] = gate;

  machine.requestRecompute();
  await flushMicrotasks();
  machine.requestRecompute();
  machine.requestRecompute();
  machine.requestRecompute(); // three duplicates while blocked -- must coalesce into exactly one rerun.

  gate.resolve();
  await flushMicrotasks();
  await flushMicrotasks();

  const computeCalls = calls.filter((c) => c === "compute").length;
  assert.equal(computeCalls, 2, "exactly one coalesced rerun, never an unbounded queue");
});
