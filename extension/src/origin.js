// PRIVON MV3 extension -- the ONLY place the exact five-origin supported allowlist is checked
// client-side (mirrors Privon.App.WebTargetGate's own EXACT_ORIGIN_POLICY: exact, case-sensitive-
// after-normalization string equality against exactly five constants, never Contains/EndsWith/
// StartsWith, never a wildcard, never subdomain acceptance, never path/query/fragment involvement).

export const SUPPORTED_ORIGINS = Object.freeze([
  "https://chatgpt.com",
  "https://claude.ai",
  "https://gemini.google.com",
  "https://grok.com",
  "https://chat.deepseek.com",
]);

const SUPPORTED_ORIGIN_SET = new Set(SUPPORTED_ORIGINS);

/**
 * Classifies a tab URL against the exact five-origin allowlist.
 *
 * Returns { resolution: "Unsupported" | "Resolved", origin: string | null } -- NEVER
 * "Unresolved" (that local-state concept belongs exclusively to the caller, e.g. "no active tab
 * could be queried at all"; this pure function only classifies a URL it was actually handed).
 *
 * PRIVACY: for any URL that is not an exact match, `origin` is always null -- the raw url
 * parameter is consumed here and never returned, retained, logged, or forwarded by this function.
 * `new URL(url).origin` extracts exactly `scheme://host[:port]` -- structurally never a path,
 * query, or fragment, so even a supported page's URL cannot leak more than its bare origin through
 * this function's own return value.
 */
export function classifyOrigin(url) {
  if (typeof url !== "string" || url.length === 0) {
    return { resolution: "Unsupported", origin: null };
  }

  let origin;
  try {
    origin = new URL(url).origin;
  } catch {
    return { resolution: "Unsupported", origin: null };
  }

  if (SUPPORTED_ORIGIN_SET.has(origin)) {
    return { resolution: "Resolved", origin };
  }

  return { resolution: "Unsupported", origin: null };
}
