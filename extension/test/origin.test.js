import { test } from "node:test";
import assert from "node:assert/strict";
import { classifyOrigin, SUPPORTED_ORIGINS } from "../src/origin.js";

// Case 1: exact five origins accepted.
test("case1: exactly the five frozen origins classify as Resolved with the exact origin", () => {
  for (const origin of SUPPORTED_ORIGINS) {
    const result = classifyOrigin(origin + "/");
    assert.equal(result.resolution, "Resolved");
    assert.equal(result.origin, origin);
  }
  assert.deepEqual(SUPPORTED_ORIGINS, [
    "https://chatgpt.com",
    "https://claude.ai",
    "https://gemini.google.com",
    "https://grok.com",
    "https://chat.deepseek.com",
  ]);
});

// Case 2: near-miss origins rejected.
test("case2: near-miss origins are Unsupported, never Resolved", () => {
  const nearMisses = [
    "https://www.chatgpt.com",
    "https://chatgpt.com.evil.example",
    "http://chatgpt.com", // wrong scheme
    "https://chatgpt.com:8443", // non-default port changes the origin
    "https://CHATGPT.COM.evil.example",
    "https://notchatgpt.com",
    "https://chat.openai.com", // real redirect host, still not one of the five exact origins
  ];
  for (const url of nearMisses) {
    const result = classifyOrigin(url);
    assert.equal(result.resolution, "Unsupported", `expected Unsupported for ${url}`);
    assert.equal(result.origin, null, `expected null origin for ${url}`);
  }
});

// Case 3 (origin-classifier half): unsupported classification never carries any raw-URL-derived
// value beyond the literal "Unsupported" token + null origin.
test("case3: unsupported result never carries a raw URL fragment of any kind", () => {
  const result = classifyOrigin("https://chatgpt.com.evil.example/secret/path?x=1#frag");
  assert.equal(result.resolution, "Unsupported");
  assert.equal(result.origin, null);
  assert.deepEqual(Object.keys(result).sort(), ["origin", "resolution"]);
});

// Case 4: no path/query/fragment leaks into the classification for a SUPPORTED origin either.
test("case4: a supported origin's path/query/fragment never appear in the classified origin", () => {
  const result = classifyOrigin("https://claude.ai/chat/abc123?ref=email#section-2");
  assert.equal(result.resolution, "Resolved");
  assert.equal(result.origin, "https://claude.ai");
  assert.equal(result.origin.includes("/chat"), false);
  assert.equal(result.origin.includes("ref="), false);
  assert.equal(result.origin.includes("section-2"), false);
});

// Case 23: malformed/unparsable URL becomes Unsupported without raw leak.
test("case23: malformed/unparsable/absent URL classifies as Unsupported with no leak", () => {
  const inputs = [undefined, null, "", "not a url at all", "chrome://newtab", "javascript:alert(1)"];
  for (const input of inputs) {
    const result = classifyOrigin(input);
    // chrome:// and javascript: ARE technically parsable by the URL constructor with their own
    // scheme as origin -- neither is one of the five frozen https origins, so both still classify
    // Unsupported; this loop's real assertion is simply that nothing throws and nothing resolves.
    assert.equal(result.resolution, "Unsupported", `expected Unsupported for ${JSON.stringify(input)}`);
    assert.equal(result.origin, null);
  }
});
