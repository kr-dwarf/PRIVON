import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const extensionRoot = path.resolve(__dirname, "..");
const manifest = JSON.parse(fs.readFileSync(path.join(extensionRoot, "manifest.json"), "utf8"));
const srcDir = path.join(extensionRoot, "src");
const srcFiles = fs.readdirSync(srcDir).filter((f) => f.endsWith(".js"));
const srcContents = Object.fromEntries(srcFiles.map((f) => [f, fs.readFileSync(path.join(srcDir, f), "utf8")]));

// Strips // line comments and /* */ block comments before forbidden-token scanning below -- a
// scan over raw source would otherwise flag this codebase's OWN doc comments that explicitly
// DISCLAIM an API (e.g. "never references ... innerText ...") as if they were real usages.
// Deliberately naive (does not understand string literals containing "//") -- sufficient here
// because none of the forbidden tokens below ever legitimately appear inside a string literal in
// this codebase either.
function stripComments(source) {
  return source.replace(/\/\*[\s\S]*?\*\//g, "").replace(/\/\/.*$/gm, "");
}

const srcContentsNoComments = Object.fromEntries(
  Object.entries(srcContents).map(([f, content]) => [f, stripComments(content)]),
);
const allSource = Object.values(srcContentsNoComments).join("\n");

test("manifest declares no forbidden permission", () => {
  const permissions = new Set([...(manifest.permissions ?? []), ...(manifest.optional_permissions ?? [])]);
  const forbidden = [
    "content_scripts", "scripting", "webRequest", "webRequestBlocking",
    "debugger", "clipboardRead", "clipboardWrite", "downloads", "history", "cookies",
  ];
  for (const capability of forbidden) {
    assert.equal(permissions.has(capability), false, `forbidden permission present: ${capability}`);
    assert.equal(capability in manifest, false, `forbidden top-level manifest key present: ${capability}`);
  }
});

test("manifest declares nativeMessaging and exactly the five origins as host_permissions", () => {
  assert.deepEqual(manifest.permissions, ["nativeMessaging"]);
  assert.deepEqual(manifest.host_permissions, [
    "https://chatgpt.com/*",
    "https://claude.ai/*",
    "https://gemini.google.com/*",
    "https://grok.com/*",
    "https://chat.deepseek.com/*",
  ]);
});

test("manifest host_permissions never equals <all_urls> and never uses a bare wildcard host", () => {
  for (const pattern of manifest.host_permissions) {
    assert.notEqual(pattern, "<all_urls>");
    assert.equal(/^[a-z-]+:\/\/\*\//.test(pattern), false, `wildcard host pattern: ${pattern}`);
  }
});

test("manifest does not hard-code the E5B dev-measurement extension ID or a 'key' pin", () => {
  const raw = JSON.stringify(manifest);
  assert.equal(raw.includes("aippijooaannjccdplmnfpjbgjppkoaa"), false);
  assert.equal("key" in manifest, false);
});

// Case 24: no composer/DOM API dependency anywhere in the production runtime source.
test("case24: no DOM, composer, or forbidden browser API surface anywhere in src/", () => {
  const forbiddenTokens = [
    "document.", "innerText", "innerHTML", "contentEditable", "execCommand",
    "chrome.scripting", "chrome.webRequest", "chrome.debugger",
    "chrome.tabCapture", "chrome.desktopCapture", "navigator.clipboard",
    "content_scripts", "executeScript", "insertCSS",
  ];
  for (const token of forbiddenTokens) {
    assert.equal(allSource.includes(token), false, `forbidden token found in src/: ${token}`);
  }
});

test("source never references raw page content fields (title, favIconUrl) or logs tab.url", () => {
  const forbiddenTokens = ["tab.title", "favIconUrl", "console.log(tab", "console.log(url"];
  for (const token of forbiddenTokens) {
    assert.equal(allSource.includes(token), false, `forbidden token found in src/: ${token}`);
  }
});

test("session.js only ever queries the active/last-focused tab, never an arbitrary/unfiltered tab list", () => {
  const session = srcContents["session.js"];
  assert.equal(session.includes("active: true"), true);
  assert.equal(session.includes("lastFocusedWindow: true"), true);
});

test("background.js contains no logic of its own beyond wiring PrivonSession to the real chrome global", () => {
  const background = fs.readFileSync(path.join(srcDir, "background.js"), "utf8");
  assert.equal(background.includes("chrome.tabs"), false);
  assert.equal(background.includes("chrome.runtime.connectNative"), false);
  assert.equal(background.includes("new PrivonSession"), true);
});

test("no production Chrome/Edge Web Store extension identity is hard-coded anywhere in src/", () => {
  // The only 32-lowercase-a-p-letter string this codebase ever recognizes as an extension-id SHAPE
  // is the E5B dev-measurement ID itself, already checked above; this test additionally proves no
  // OTHER 32-char [a-p] literal (a plausible future store ID guess) appears anywhere in src/.
  const idShapePattern = /"[a-p]{32}"/;
  assert.equal(idShapePattern.test(allSource), false, "a Chrome-extension-ID-shaped literal was found in src/");
});
