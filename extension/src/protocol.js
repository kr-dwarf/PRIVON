// PRIVON MV3 extension -- exact wire-message builders/parsers for the FROZEN Native Messaging
// protocol (Privon.Browser.WebProtocolMessage / WebFrameEncoder / WebFrameDecoder, .NET side --
// Gate 031F1R/031F6C). chrome.runtime.connectNative already performs the 4-byte length-prefixed
// framing and UTF-8/JSON layer for us: port.postMessage/onMessage exchange plain JS objects, never
// raw bytes -- so this module's only job is to build/validate the EXACT JSON payload shape those
// C# types define, field-for-field, never a second wire representation.
//
// Six frozen message types (Privon.Browser.WebProtocolMessageType): Hello, HelloAck,
// StateInvalidate, StateAssert, ChallengeRequest, ChallengeResponse. The extension only ever SENDS
// Hello, StateInvalidate, StateAssert, ChallengeResponse, and only ever RECEIVES HelloAck,
// ChallengeRequest -- mirroring the native host's own reversed role exactly.

export const PROTOCOL_VERSION = 1;

export const FOCUS = Object.freeze({
  UNRESOLVED: "Unresolved",
  NOT_FOCUSED: "NotFocused",
  FOCUSED: "Focused",
});

export const ORIGIN_RESOLUTION = Object.freeze({
  UNRESOLVED: "Unresolved",
  UNSUPPORTED: "Unsupported",
  RESOLVED: "Resolved",
});

const VALID_FOCUS = new Set(Object.values(FOCUS));
const VALID_ORIGIN_RESOLUTION = new Set(Object.values(ORIGIN_RESOLUTION));

// Mirrors WebFrameDecoder/WebFrameEncoder's own frozen nonce shape exactly: 43 unpadded Base64URL
// characters, decoding to exactly 32 bytes (never trusted on length/alphabet alone).
export function isValidNonce(nonce) {
  if (typeof nonce !== "string" || nonce.length !== 43) return false;
  if (!/^[A-Za-z0-9_-]{43}$/.test(nonce)) return false;

  try {
    const standardBase64 = nonce.replace(/-/g, "+").replace(/_/g, "/") + "=";
    const decoded = atob(standardBase64);
    return decoded.length === 32;
  } catch {
    return false;
  }
}

export function buildHello() {
  return { v: PROTOCOL_VERSION, type: "Hello" };
}

export function buildStateInvalidate() {
  return { v: PROTOCOL_VERSION, type: "StateInvalidate" };
}

/** Mirrors WebFrameEncoder's StateAssert field rules exactly: origin present iff
 * originResolution === Resolved, and non-empty in that case only. */
export function buildStateAssert(focus, originResolution, origin) {
  if (!VALID_FOCUS.has(focus)) throw new Error(`Invalid focus: ${String(focus)}`);
  if (!VALID_ORIGIN_RESOLUTION.has(originResolution)) {
    throw new Error(`Invalid originResolution: ${String(originResolution)}`);
  }

  const resolved = originResolution === ORIGIN_RESOLUTION.RESOLVED;
  if (resolved && (typeof origin !== "string" || origin.length === 0)) {
    throw new Error("StateAssert with Resolved originResolution requires a non-empty origin.");
  }
  if (!resolved && origin != null) {
    throw new Error("StateAssert origin must be omitted unless originResolution is Resolved.");
  }

  const message = { v: PROTOCOL_VERSION, type: "StateAssert", focus, originResolution };
  if (resolved) message.origin = origin;
  return message;
}

/** Mirrors WebFrameEncoder's ChallengeResponse field rules: nonce plus the same focus/origin shape
 * as StateAssert. */
export function buildChallengeResponse(nonce, focus, originResolution, origin) {
  if (!isValidNonce(nonce)) throw new Error("Invalid challenge nonce shape.");

  const assertPart = buildStateAssert(focus, originResolution, origin);
  const message = {
    v: PROTOCOL_VERSION,
    type: "ChallengeResponse",
    nonce,
    focus: assertPart.focus,
    originResolution: assertPart.originResolution,
  };
  if (assertPart.origin !== undefined) message.origin = assertPart.origin;
  return message;
}

/** Validates and extracts an inbound HelloAck. Returns { accepted } or null if malformed --
 * malformed pre-handshake traffic is never treated as an accepted (or even rejected) handshake. */
export function parseHelloAck(message) {
  if (!isPlainObject(message)) return null;
  if (message.v !== PROTOCOL_VERSION) return null;
  if (message.type !== "HelloAck") return null;
  if (typeof message.accepted !== "boolean") return null;
  if (!hasOnlyKeys(message, ["v", "type", "accepted"])) return null;

  return { accepted: message.accepted };
}

/** Validates and extracts an inbound ChallengeRequest. Returns { nonce } or null if malformed. */
export function parseChallengeRequest(message) {
  if (!isPlainObject(message)) return null;
  if (message.v !== PROTOCOL_VERSION) return null;
  if (message.type !== "ChallengeRequest") return null;
  if (!isValidNonce(message.nonce)) return null;
  if (!hasOnlyKeys(message, ["v", "type", "nonce"])) return null;

  return { nonce: message.nonce };
}

function isPlainObject(value) {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function hasOnlyKeys(obj, allowedKeys) {
  const allowed = new Set(allowedKeys);
  return Object.keys(obj).every((key) => allowed.has(key));
}
