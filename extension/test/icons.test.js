import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import crypto from "node:crypto";

// PRIVON 0.3.2 Gate 032-B2B -- proves the extension actually ships the official transparent PV
// icon set declared structurally in manifest.json's own "icons" key -- never a raw substring
// search over the manifest text, and never a check that could pass merely because the required
// path text appears somewhere unrelated. Mirrors the same discipline already established and
// independently audited for the desktop branding test oracle
// (tests/Privon.App.Tests/Gate032B1_DesktopBrandingTests.cs): every oracle here is a small pure
// function that throws a descriptive Error on violation, exercised once against the real
// production artifact (expected not to throw) and once against a synthetic fixture engineered to
// violate exactly the property being checked (expected to throw) -- so this file also proves its
// own oracles actually discriminate, not just that the current manifest happens to satisfy a
// possibly-too-loose check.

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const extensionRoot = path.resolve(__dirname, "..");
const manifest = JSON.parse(fs.readFileSync(path.join(extensionRoot, "manifest.json"), "utf8"));

const EXPECTED_ICONS = Object.freeze({
  16: "icons/privon-16.png",
  32: "icons/privon-32.png",
  48: "icons/privon-48.png",
  128: "icons/privon-128.png",
});

// ==================================================================
// oracles (pure, throw-on-violation, shared by real assertions and negative probes below)
// ==================================================================

function assertManifestIconsExactly(manifestLike, expected) {
  if (typeof manifestLike.icons !== "object" || manifestLike.icons === null || Array.isArray(manifestLike.icons)) {
    throw new Error("manifest.icons must be a plain object");
  }

  const actualKeys = Object.keys(manifestLike.icons).sort();
  const expectedKeys = Object.keys(expected).sort();

  if (actualKeys.length !== expectedKeys.length || !actualKeys.every((k, i) => k === expectedKeys[i])) {
    throw new Error(
      `manifest.icons must have exactly keys [${expectedKeys.join(",")}], found [${actualKeys.join(",")}]`,
    );
  }

  for (const key of expectedKeys) {
    if (manifestLike.icons[key] !== expected[key]) {
      throw new Error(`manifest.icons["${key}"] must equal "${expected[key]}", found "${manifestLike.icons[key]}"`);
    }
  }
}

function assertNoActionSurfaceOrKeyPin(manifestLike) {
  for (const forbidden of ["action", "browser_action", "page_action", "key"]) {
    if (forbidden in manifestLike) {
      throw new Error(`manifest must not declare forbidden top-level key: ${forbidden}`);
    }
  }
}

// ==================================================================
// real manifest -- structural proof
// ==================================================================

test("manifest.icons exactly matches the four required official PV icon entries", () => {
  assert.doesNotThrow(() => assertManifestIconsExactly(manifest, EXPECTED_ICONS));
});

test("manifest still declares no action, browser_action, page_action, or key", () => {
  assert.doesNotThrow(() => assertNoActionSurfaceOrKeyPin(manifest));
});

// ==================================================================
// negative probes -- prove the oracles above actually discriminate, using synthetic in-memory
// fixtures only (never a file on disk).
// ==================================================================

test("icons oracle rejects a manifest missing the 128 icon key", () => {
  const synthetic = { icons: { 16: EXPECTED_ICONS[16], 32: EXPECTED_ICONS[32], 48: EXPECTED_ICONS[48] } };
  assert.throws(() => assertManifestIconsExactly(synthetic, EXPECTED_ICONS));
});

test("icons oracle rejects a manifest with an extra unexpected icon key", () => {
  const synthetic = { icons: { ...EXPECTED_ICONS, 256: "icons/privon-256.png" } };
  assert.throws(() => assertManifestIconsExactly(synthetic, EXPECTED_ICONS));
});

test("icons oracle rejects a manifest where an icon path points elsewhere", () => {
  const synthetic = { icons: { ...EXPECTED_ICONS, 128: "icons/wrong-path.png" } };
  assert.throws(() => assertManifestIconsExactly(synthetic, EXPECTED_ICONS));
});

test("icons oracle rejects a manifest with no icons key at all", () => {
  const synthetic = {};
  assert.throws(() => assertManifestIconsExactly(synthetic, EXPECTED_ICONS));
});

test("action-surface oracle rejects a manifest declaring action", () => {
  assert.throws(() => assertNoActionSurfaceOrKeyPin({ action: {} }));
});

test("action-surface oracle rejects a manifest declaring a key pin", () => {
  assert.throws(() => assertNoActionSurfaceOrKeyPin({ key: "some-base64-key" }));
});

// ==================================================================
// PRIVON 0.3.2 Gate 032-B2B -- asset binary identity. Exact SHA-256, not just "file exists" or
// "dimensions look right" -- a payload mutation that left the header untouched would pass a
// dimension-only check but must fail here. Mirrors the desktop branding test's
// AssertKnownPng/AssetIdentityOracle_RejectsOneByteMutated* discipline exactly, in JS.
// ==================================================================

const REQUIRED_ICON_ASSETS = Object.freeze([
  { size: 16, file: "privon-16.png", sha256: "e2e3672123bce94877381b9972021bc558c04a37864b48f82f99c44a73430231" },
  { size: 32, file: "privon-32.png", sha256: "fbb25abbbf9855d93dc3d497250b7f2ff9103bdc5f5fd1c1f40caee840780589" },
  { size: 48, file: "privon-48.png", sha256: "e3100a61ca907bdec5a632d1c8613e179da5f0bd6b7e821f1b17bbbb0389d79c" },
  { size: 128, file: "privon-128.png", sha256: "0ca845c5b89011e277ca9731123152635194573db90b6ca41297a7cf2075c985" },
]);

function readPngHeader(buffer) {
  const signature = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
  if (buffer.length < 26 || !buffer.subarray(0, 8).equals(signature)) {
    throw new Error("Data is not a valid PNG (bad signature or too short).");
  }
  return {
    width: buffer.readUInt32BE(16),
    height: buffer.readUInt32BE(20),
    bitDepth: buffer.readUInt8(24),
    colorType: buffer.readUInt8(25),
  };
}

function assertKnownPngAsset(buffer, expectedSha256, expectedSize) {
  const actualHash = crypto.createHash("sha256").update(buffer).digest("hex");
  if (actualHash !== expectedSha256) {
    throw new Error(`SHA-256 mismatch: expected ${expectedSha256}, got ${actualHash}`);
  }

  const { width, height, bitDepth, colorType } = readPngHeader(buffer);
  if (width !== expectedSize || height !== expectedSize) {
    throw new Error(`Dimension mismatch: expected ${expectedSize}x${expectedSize}, got ${width}x${height}`);
  }
  if (bitDepth !== 8) {
    throw new Error(`Bit depth mismatch: expected 8, got ${bitDepth}`);
  }
  if (colorType !== 6) {
    throw new Error(`PNG color type mismatch: expected 6 (RGBA), got ${colorType}`);
  }
}

for (const asset of REQUIRED_ICON_ASSETS) {
  test(`extension/icons/${asset.file} matches canonical identity (sha256, ${asset.size}x${asset.size}, 8-bit RGBA)`, () => {
    const buffer = fs.readFileSync(path.join(extensionRoot, "icons", asset.file));
    assert.doesNotThrow(() => assertKnownPngAsset(buffer, asset.sha256, asset.size));
  });
}

test("asset identity oracle rejects a one-byte-mutated icon (128px)", () => {
  const asset = REQUIRED_ICON_ASSETS.find((a) => a.size === 128);
  const buffer = fs.readFileSync(path.join(extensionRoot, "icons", asset.file));
  const mutated = Buffer.from(buffer);
  mutated[mutated.length - 1] ^= 0xff; // flips a payload byte far from the header -- dimensions
  assert.throws(() => assertKnownPngAsset(mutated, asset.sha256, asset.size)); // still parse fine
});

test("asset identity oracle rejects a one-byte-mutated icon (16px)", () => {
  const asset = REQUIRED_ICON_ASSETS.find((a) => a.size === 16);
  const buffer = fs.readFileSync(path.join(extensionRoot, "icons", asset.file));
  const mutated = Buffer.from(buffer);
  mutated[mutated.length - 1] ^= 0xff;
  assert.throws(() => assertKnownPngAsset(mutated, asset.sha256, asset.size));
});

test("extension/icons/privon-128.png is byte-identical to the frozen desktop canonical 128px asset", () => {
  const extensionBuffer = fs.readFileSync(path.join(extensionRoot, "icons", "privon-128.png"));
  const desktopAssetPath = path.resolve(
    extensionRoot,
    "..",
    "src",
    "Privon.App",
    "Assets",
    "privon-icon-128.png",
  );
  const desktopBuffer = fs.readFileSync(desktopAssetPath);
  assert.deepEqual(extensionBuffer, desktopBuffer);
});
