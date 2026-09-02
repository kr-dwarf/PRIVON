import { test } from "node:test";
import assert from "node:assert/strict";
import {
  PROTOCOL_VERSION,
  buildHello,
  buildStateInvalidate,
  buildStateAssert,
  buildChallengeResponse,
  parseHelloAck,
  parseChallengeRequest,
  isValidNonce,
} from "../src/protocol.js";

function makeValidNonce() {
  // 32 zero bytes, base64url-encoded without padding -- guaranteed to decode back to exactly 32 bytes.
  const bytes = new Uint8Array(32);
  const bin = String.fromCharCode(...bytes);
  return btoa(bin).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

test("buildHello/buildStateInvalidate carry exactly {v,type}, no extra fields", () => {
  assert.deepEqual(buildHello(), { v: PROTOCOL_VERSION, type: "Hello" });
  assert.deepEqual(buildStateInvalidate(), { v: PROTOCOL_VERSION, type: "StateInvalidate" });
});

test("buildStateAssert(Resolved) includes exactly v/type/focus/originResolution/origin", () => {
  const msg = buildStateAssert("Focused", "Resolved", "https://claude.ai");
  assert.deepEqual(msg, {
    v: PROTOCOL_VERSION,
    type: "StateAssert",
    focus: "Focused",
    originResolution: "Resolved",
    origin: "https://claude.ai",
  });
});

test("buildStateAssert(Unsupported) omits origin entirely (not present, not null)", () => {
  const msg = buildStateAssert("NotFocused", "Unsupported", null);
  assert.deepEqual(msg, {
    v: PROTOCOL_VERSION,
    type: "StateAssert",
    focus: "NotFocused",
    originResolution: "Unsupported",
  });
  assert.equal("origin" in msg, false);
});

test("buildStateAssert rejects Resolved without a non-empty origin", () => {
  assert.throws(() => buildStateAssert("Focused", "Resolved", null));
  assert.throws(() => buildStateAssert("Focused", "Resolved", ""));
});

test("buildStateAssert rejects a non-Resolved originResolution carrying an origin", () => {
  assert.throws(() => buildStateAssert("Focused", "Unsupported", "https://claude.ai"));
  assert.throws(() => buildStateAssert("Focused", "Unresolved", "https://claude.ai"));
});

test("buildStateAssert rejects an unknown focus/originResolution value", () => {
  assert.throws(() => buildStateAssert("Bogus", "Resolved", "https://claude.ai"));
  assert.throws(() => buildStateAssert("Focused", "Bogus", null));
});

test("buildChallengeResponse carries nonce plus the exact StateAssert-shaped fields", () => {
  const nonce = makeValidNonce();
  const msg = buildChallengeResponse(nonce, "Focused", "Resolved", "https://chatgpt.com");
  assert.deepEqual(msg, {
    v: PROTOCOL_VERSION,
    type: "ChallengeResponse",
    nonce,
    focus: "Focused",
    originResolution: "Resolved",
    origin: "https://chatgpt.com",
  });
});

test("buildChallengeResponse rejects a structurally invalid nonce", () => {
  assert.throws(() => buildChallengeResponse("not-a-real-nonce", "Focused", "Resolved", "https://chatgpt.com"));
  assert.throws(() => buildChallengeResponse(makeValidNonce().slice(0, 42), "Focused", "Resolved", "https://chatgpt.com")); // wrong length
  assert.throws(() => buildChallengeResponse(makeValidNonce().slice(0, 42) + "!", "Focused", "Resolved", "https://chatgpt.com")); // invalid char
});

test("parseHelloAck accepts exactly {v,type,accepted}", () => {
  assert.deepEqual(parseHelloAck({ v: 1, type: "HelloAck", accepted: true }), { accepted: true });
  assert.deepEqual(parseHelloAck({ v: 1, type: "HelloAck", accepted: false }), { accepted: false });
});

test("parseHelloAck rejects malformed inputs", () => {
  assert.equal(parseHelloAck(null), null);
  assert.equal(parseHelloAck("HelloAck"), null);
  assert.equal(parseHelloAck({ v: 2, type: "HelloAck", accepted: true }), null); // wrong version
  assert.equal(parseHelloAck({ v: 1, type: "Hello", accepted: true }), null); // wrong type
  assert.equal(parseHelloAck({ v: 1, type: "HelloAck", accepted: "true" }), null); // non-boolean
  assert.equal(parseHelloAck({ v: 1, type: "HelloAck", accepted: true, extra: 1 }), null); // extra field
  assert.equal(parseHelloAck({ v: 1, type: "HelloAck" }), null); // missing required field
});

test("parseChallengeRequest accepts exactly {v,type,nonce} with a structurally valid nonce", () => {
  const nonce = makeValidNonce();
  assert.deepEqual(parseChallengeRequest({ v: 1, type: "ChallengeRequest", nonce }), { nonce });
});

test("parseChallengeRequest rejects malformed inputs", () => {
  const nonce = makeValidNonce();
  assert.equal(parseChallengeRequest({ v: 1, type: "ChallengeRequest", nonce: "short" }), null);
  assert.equal(parseChallengeRequest({ v: 1, type: "ChallengeRequest", nonce, extra: "x" }), null);
  assert.equal(parseChallengeRequest({ v: 1, type: "HelloAck", nonce }), null);
  assert.equal(parseChallengeRequest(undefined), null);
});

test("isValidNonce requires exactly 43 base64url characters decoding to exactly 32 bytes", () => {
  assert.equal(isValidNonce(makeValidNonce()), true);
  assert.equal(isValidNonce("too-short"), false);
  assert.equal(isValidNonce(makeValidNonce().slice(0, 42)), false); // 42 chars: wrong length
  assert.equal(isValidNonce(makeValidNonce().slice(0, 42) + "!"), false); // 43 chars, invalid alphabet
  assert.equal(isValidNonce(123), false);
  assert.equal(isValidNonce(null), false);
});
