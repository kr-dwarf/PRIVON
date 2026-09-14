[English](./README.md) | [한국어](./README.ko.md)

---

# PRIVON

**PRIVON — turn on privacy protection before you use AI.**

> PRIVON is a public beta, distributed for real-world validation. It does not claim
> 1.0-level stability. This document describes the v0.3.2 candidate while release
> finalization is in progress. It does not claim that v0.3.2 has been publicly published;
> see [Release provenance](#release-provenance) for the verified release state.

---

## What PRIVON Is

PRIVON is a local-first privacy protection utility for Windows.

- If text you copy to the clipboard contains a supported personal-information pattern
  (such as a phone number or a Korean resident registration number), PRIVON detects and
  protects it locally, in the clipboard, **before** you manually paste it into a supported
  AI target. This includes the authorized ChatGPT and Claude desktop identities and the
  exact Chrome websites listed under [Supported Scope](#supported-scope).
- All processing happens on your own PC. Clipboard content is never sent to an external
  server.
- PRIVON continues to prioritize the **Korean (KR) usage environment** and the
  **clipboard path**. Desktop and browser identities are authenticated narrowly; a
  similarly named executable or an unapproved website is not accepted. Unsupported or
  uncertain identities fail closed, and PRIVON aims not to interfere with ordinary
  clipboard use outside the supported paths.

---

## Quick Start (30 seconds)

No development tools or command line needed — just these steps.

1. Download the latest `PRIVON-*-win-x64.zip` package from the GitHub Releases page.
2. Right-click the downloaded zip file → **Extract All**.
3. Double-click `PRIVON.exe` in the extracted folder to run it.
4. You'll know it's running correctly when the PRIVON icon appears in the notification
   area (system tray) at the bottom right of your screen.
5. Use a supported desktop target as usual. For supported Chrome websites, install the
   approved PRIVON Chrome extension and open **Settings** to complete **Connect Chrome**
   when offered. Connection changes are always an explicit user action.

> PRIVON acts only when the current foreground identity is an authorized desktop target or
> an authenticated Chrome tab at an exact supported origin. Other applications and browser
> sites are not authorized.

---

## Example

The example below uses **synthetic, made-up data** — not real personal information.

**Original text you copy**

```
문의 전화 010-1234-5678
```

**Clipboard content after PRIVON protects it**

```
문의 전화 [전화번호1]
```

The clipboard itself is changed before you paste, so ChatGPT receives the protected form,
not the original phone number.

> Note for English-speaking readers: the bracketed placeholder label itself
> (`[전화번호1]`, "phone number 1") is currently always in Korean, regardless of the
> language of the surrounding text you copy — this is current behavior, not a
> translation gap in this README.

---

## Supported Scope

- Windows 10 / 11 (64-bit, win-x64)
- Desktop targets: the currently authorized **ChatGPT Windows Desktop Microsoft Store
  package identity** and the authenticated **Claude Desktop** application. Name-only
  lookalikes and unrecognized identities are not accepted.
- Chrome Web targets: only `https://chatgpt.com`, `https://claude.ai`,
  `https://gemini.google.com`, `https://grok.com`, and `https://chat.deepseek.com`, through
  the approved PRIVON Chrome extension and an authenticated local connection. Substituted,
  malformed, or other origins are not supported.
- Microsoft Edge integration is unavailable and deferred.
- Antigravity is unsupported.
- Korean (KR) usage prioritized
- Clipboard path (clipboard-first) prioritized, now including content you copied before
  switching to ChatGPT
- All processing performed locally
- No signup required

---

## Supported Personal Information Categories

PRIVON currently attempts to detect and protect the following categories:

- Phone numbers
- Email addresses
- Korean resident registration numbers (주민등록번호)
- Card numbers
- Bank account numbers
- Secret/token-like values (e.g. API keys)
- IP addresses
- MAC addresses
- GPS/location coordinates

> Even for these categories, detection can miss or misjudge values depending on context.
> PRIVON does not aim for "perfect detection" — please also read
> [What PRIVON Does Not Guarantee](#what-privon-does-not-guarantee)
> below.

---

## When Confirmation Is Needed (NeedsDecision)

For values judged to carry higher sensitivity, PRIVON does not replace them immediately.
Instead, it shows you a confirmation window and lets you decide.

This confirmation window offers exactly one action: **Protect All**. Clicking it
protects every item that needed a decision, then closes the window.

---

## Settings

Right-click the PRIVON tray icon → **Settings** to open the Settings window. Currently
live:

- Turn Phone protection on or off
- Turn Email protection on or off
- Add or remove an exact-value exception for a Phone number or Email address (a specific
  value you never want protected)
- Reset protection scope back to the default (every category on)
- Reset your exception list
- Your choices are saved locally and persist across restarts
- If PRIVON's encrypted local storage is currently unavailable, Settings honestly shows
  this degraded state rather than silently pretending your changes were saved

Not yet live: Name, Address, and Company have no working detector yet, so they have no
Settings control of their own — there is nothing to turn on or off for them. Settings has
no Undo/Restore for a change you already made; a reset restores defaults, it does not step
back through history.

### Chrome connection and reconnection

- **Not connected:** On a fresh installation, PRIVON performs one read-only readiness
  check and may show **Connect Chrome**. Starting PRIVON or merely opening Settings does
  not create registry or manifest entries. Choose **Connect Chrome** explicitly to connect.
- **Connected:** The owned Native Messaging registration and manifest match the current
  PRIVON executable and approved extension.
- **Connection needs updating:** Moving the portable PRIVON executable can leave an owned
  registration pointing to its old path. PRIVON may notify you, but clicking the
  notification or opening Settings does not repair anything. Choose **Reconnect Chrome**
  explicitly after confirming the current PRIVON location.
- **Blocked or unavailable:** PRIVON does not take over another program's registration and
  does not silently adopt an ambiguous leftover manifest. An inspection failure is not
  shown as connected. Resolve the conflicting owner/file first; do not delete unfamiliar
  registrations merely to force a connection.

Chrome connection actions apply only to Chrome. Edge remains unavailable.

---

## What PRIVON Does Not Guarantee

PRIVON does not claim or guarantee any of the following:

- Protection for arbitrary applications or websites. Only the exact desktop identities
  and Chrome origins in [Supported Scope](#supported-scope) are authorized.
- Protection in Edge or Antigravity; both are outside the current supported scope.
- Full anonymization.
- Regulatory/privacy-law compliance certification.
- Detection of every possible identifier beyond the categories explicitly listed above.
- Interception or monitoring of ChatGPT's network traffic.
- Automatic sending of messages — PRIVON never sends anything on your behalf; you still
  paste and send manually.
- Protection for text typed directly into the composer (direct composer typing). PRIVON's
  officially supported and guaranteed path is the **clipboard path**. Direct-typing
  protection exists internally only as a limited/experimental capability and is **not**
  part of the protection guarantee.
- That a paste, or a send, actually occurred. PRIVON protects clipboard content; it does
  not track or confirm what you subsequently did with it.
- Any persistent "safe" or "verified" state. PRIVON does not display a lasting
  "protected"/"verified" indicator anywhere in the UI (the tray icon's tooltip, for
  example, only indicates that PRIVON is running — not that any particular content has
  been protected or verified).
- That pasted content in the ChatGPT composer definitely matches the protected clipboard
  text. An internal capability exists to verify that the Windows clipboard write itself
  succeeded and can be read back correctly — but that is a narrower guarantee than
  confirming what ultimately ends up in the ChatGPT composer after a manual paste. That
  broader composer-verification capability exists internally but is **not** exposed as a
  public feature.

---

## Privacy Design

Only claims verified by the current code and test suite are listed here.

- All detection and protection processing is performed locally.
- No telemetry is sent.
- No usage analytics are collected.
- No third-party crash-reporting SDK is used.
- Diagnostic logs never record raw personal information (raw PII), and diagnostic logging
  itself is **off by default**. Running PRIVON normally creates no log file at all. For
  explicit QA/troubleshooting purposes only, diagnostics can be turned on by setting the
  environment variable `PRIVON_ENABLE_DIAGNOSTICS=1` before launching PRIVON — even then,
  logs remain metadata-only (no raw clipboard text, detected values, or protected
  replacement text).
- Local state (trusted-value list, exception list, etc.) is stored encrypted under
  `%LocalAppData%\PRIVON`.
- Within its currently implemented scope, PRIVON discards sensitive in-progress memory
  state (such as a pending confirmation decision) immediately on Windows session lock
  (Win+L).

---

## Windows SmartScreen Warning

PRIVON is not yet code-signed. Because of this, Windows may show a SmartScreen
warning about an "unknown publisher." This is expected behavior, and is common for
software that isn't code-signed yet.

If you obtained the file from the **official GitHub Release**, you can proceed as follows:

1. Click **More info** in the warning dialog.
2. Click **Run anyway**.

You do not need to, and should not, disable Windows Defender or SmartScreen itself.

---

## Exiting PRIVON

Right-click the PRIVON tray icon in the notification area, then select **Exit** to close
the program. **Exit only closes PRIVON; it is not an uninstall or cleanup action.**

### Removing a portable copy and persistent state

Before deleting the extracted folder, use the tray menu to turn **Auto-start** off. The
current UI does not provide a general removal action for the Chrome Native Messaging
registration. Do not delete an unfamiliar registry entry or manifest that PRIVON reports
as foreign or conflicting.

Deleting `PRIVON.exe` or its folder alone may leave Windows Auto-start registration, the
Chrome Native Messaging registration and manifest, encrypted product-local state under
`%LocalAppData%\PRIVON`, opt-in diagnostic logs if diagnostics were enabled, and the
installed Chrome extension and its browser-managed state. Browser extension removal is
performed in Chrome. Product-local files and any remaining owned registration may require
manual cleanup. There is no installer or uninstaller in v0.3.2.

---

## Auto-start (Optional)

PRIVON can optionally launch automatically when you log in to Windows.

- This is **off by default** — PRIVON does not add itself to Windows startup unless you
  explicitly turn it on.
- You can turn it on or off from the PRIVON tray icon menu at any time.
- When enabled, it is registered only for your current Windows user account — no
  administrator privileges are required, and no other user account on the machine is
  affected.
- If you later move or rename the extracted PRIVON folder while Auto-start is enabled,
  PRIVON will not falsely report Auto-start as still working — it fails closed and shows
  Auto-start as off. Simply turn it back on from the new location to re-register it.

Native Messaging and Auto-start store separate absolute executable paths. After moving
PRIVON, use **Reconnect Chrome** for the Chrome connection and explicitly toggle
Auto-start from the new location. Neither path repairs itself silently.

Running PRIVON a second time while it's already running does not start a duplicate copy —
the newer launch simply closes, and your original PRIVON instance (tray icon, clipboard
protection) keeps running unchanged.

---

## Beta Status & Feedback

PRIVON is a **public beta** distributed for real-world validation. It does not
yet guarantee 1.0-level stability, and unexpected behavior may occur. If you find a
problem, please let us know via a GitHub Issue or the Discussions tab.

---

## Building From Source

> The steps below are not for ordinary users — they're for developers who want to build
> PRIVON themselves.

### Requirements

- Windows 10/11
- .NET 10 SDK

### Restore / Build / Test

```powershell
dotnet restore PRIVON.slnx
dotnet build PRIVON.slnx -c Release
dotnet test PRIVON.slnx -c Release
```

### Publishing a build (ordinary developer publish)

```powershell
dotnet publish src/Privon.App/Privon.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:PublishReadyToRun=false `
  -p:DebugType=None `
  -o publish/win-x64
```

This produces a **self-contained** win-x64 build — end users do not need to separately
install the .NET runtime to run it. This is an ordinary local developer publish, distinct
from certified release packaging below — it performs no clean-source check, no provenance
verification, and produces no ZIP/checksum.

### Certified release packaging

`tools/publish-release.ps1` is the hardened, certified release packaging path: it verifies
the working tree is clean, builds exclusively from an isolated detached worktree pinned to
an exact Git commit, verifies the built executable actually embeds both the requested
version and that exact commit's SHA, scans for forbidden artifacts (test binaries, PDBs,
local user data files), and only then produces a ZIP + SHA-256 checksum. `-Version` is
required — there is no default:

```powershell
.\tools\publish-release.ps1 -Version 0.3.2
```

(The command above shows the invocation shape. This README does not claim that a certified
v0.3.2 desktop release package has been published.)

---

## Verification Status

- PRIVON's release gate requires the full local automated test suite
  (Core/Detection/Windows/Storage/App) to pass 100% green, with 0 build warnings and 0
  build errors, before a release candidate is packaged. These are local automated tests;
  this repository does not currently run GitHub Actions or any other CI. (Exact, dated
  pass counts belong in release evidence/release notes, not this evergreen document.)
- 320 real trials writing to the actual Windows clipboard: lower/medium-risk personal
  information was automatically protected and verified in every case that required it;
  higher-risk (NeedsDecision) values were correctly left for manual confirmation rather
  than silently auto-protected.
- Real Windows + real ChatGPT Windows Desktop clipboard-protection smoke test: passed,
  including copying content in another application before switching to ChatGPT.
- Historical desktop smoke checks confirmed that unsupported applications were not
  interfered with. Chrome Web support has its own extension and authorization tests.
- Windows session lock (Win+L) behavior smoke test: passed.
- Optional Auto-start, tested through an actual Windows restart/login with it turned on,
  and again with it turned off: passed both ways.
- Running PRIVON a second time while already running (no duplicate instance): passed.
- Release publish artifact launch smoke test: passed.

This verification reflects results within the tested scope and conditions — it does not
mean "works perfectly in every environment."

---

## Release provenance

<!-- FIRST_PUBLIC_RELEASE_TIMESTAMP: 2026-08-22T18:24:41+09:00 -->

**v0.1.0-beta** — first public release

- Published: August 22, 2026
- First public source commit: `98a5c2d`
- Tagged commit: `1380d44`
- Release ZIP SHA-256: `d1417848847f5f2140bbf619662973ef69c86dff65ecd45607db8acf7699281f`

**v0.2.0-beta**

- Tagged: August 23, 2026
- Tagged commit: `358c990`

**v0.2.1**

- Historical candidate; retained here as an earlier provenance record.

**v0.3.2 candidate**

- Current source is in release finalization; this is not a claim of public publication.
- The Chrome Web Store 0.3.2 package was submitted for review with auto-publish disabled.
- Exact submitted-package provenance is recorded in
  [`docs/release-0.3.2-store-provenance.md`](./docs/release-0.3.2-store-provenance.md).

---

## License

PRIVON's source code is publicly visible on GitHub and is distributed under the
**PolyForm Shield 1.0.0** license.

- This is **not** an OSI-approved open-source license — it is a **source-available**
  license. Describing PRIVON as "open source" would not be accurate.
- Ordinary use, source inspection, modification, and GitHub-style collaboration (forks,
  pull requests, etc.) are permitted to the extent the license allows.
- However, using PRIVON to provide a product or service that **competes** with PRIVON (or
  with another product the licensor provides using PRIVON) is restricted by the license.
- For the exact, binding terms, always refer to the [`LICENSE`](./LICENSE) file at the
  repository root. This section is a summary only — the `LICENSE` text governs.
