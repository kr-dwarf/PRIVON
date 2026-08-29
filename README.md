[English](./README.md) | [한국어](./README.ko.md)

---

# PRIVON

**PRIVON — turn on privacy protection before you use AI.**

> PRIVON is a public beta, distributed for real-world validation. It does not claim
> 1.0-level stability. This document describes the current codebase — the v0.2.1 release
> candidate. That is not a claim that v0.2.1 has been published as a tagged release; see
> [Release provenance](#release-provenance) for what has actually been published.

---

## What PRIVON Is

PRIVON is a local-first privacy protection utility for Windows.

- If text you copy to the clipboard contains a supported personal-information pattern
  (such as a phone number or a Korean resident registration number), PRIVON detects and
  protects it locally, in the clipboard, **before** you manually paste it into ChatGPT —
  even if you copied that text while a different application was active, as long as the
  supported ChatGPT Windows Desktop identity (see [Supported Scope](#supported-scope)) is
  the active window by the time you paste.
- All processing happens on your own PC. Clipboard content is never sent to an external
  server.
- PRIVON continues to prioritize the **Korean (KR) usage environment** and the
  **clipboard path**. The supported protection target is the currently supported
  Microsoft Store package identity of **ChatGPT Windows Desktop** — not ChatGPT Web
  (browser-based), and not every application or executable that happens to be named or
  process-named "ChatGPT." An unsupported desktop identity fails closed (PRIVON does not
  protect it), and PRIVON aims not to interfere with your ordinary clipboard use outside
  the one supported path.

---

## Quick Start (30 seconds)

No development tools or command line needed — just these steps.

1. Download the latest `PRIVON-*-win-x64.zip` package from the GitHub Releases page.
2. Right-click the downloaded zip file → **Extract All**.
3. Double-click `PRIVON.exe` in the extracted folder to run it.
4. You'll know it's running correctly when the PRIVON icon appears in the notification
   area (system tray) at the bottom right of your screen.
5. Now use ChatGPT as usual. Whenever ChatGPT Windows Desktop is (or becomes) the
   currently active (foreground) window, PRIVON checks the current clipboard content and
   protects it, if needed, before you paste — this works even if you copied the text a
   moment earlier while a different application was active.

> PRIVON only acts based on which window currently has focus (i.e., whether it's ChatGPT
> Windows Desktop or not). While you're using any other application — including ChatGPT in
> a web browser — PRIVON does not read or change the clipboard at all.

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
- Target application: the **currently supported Microsoft Store package identity of
  ChatGPT Windows Desktop**. ChatGPT Web / browser-based ChatGPT, any other executable
  that merely happens to be named or process-named "ChatGPT," and any other/older/
  unrecognized ChatGPT Desktop package identity are **not** supported protection
  targets — an unsupported identity fails closed rather than falling back to
  name-only recognition.
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

---

## What PRIVON Does Not Guarantee

PRIVON does not claim or guarantee any of the following:

- Protection for ChatGPT Web (browser-based) or any other application. The only
  supported protection target is the currently supported Microsoft Store package
  identity of **ChatGPT Windows Desktop** — see [Supported Scope](#supported-scope).
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
the program.

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
.\tools\publish-release.ps1 -Version 0.2.1
```

(No certified v0.2.1 package has been produced as of this document — the command above
shows the invocation shape, not a claim that such a package already exists.)

---

## Verification Status

- PRIVON's release gate requires the full local automated test suite
  (Core/Detection/Windows/Storage/App) to pass 100% green, with 0 build warnings and 0
  build errors, before a release candidate is packaged — the current v0.2.1 candidate has
  passed this full regression. These are local automated tests run during development;
  this repository does not currently run GitHub Actions or any other CI. (Exact, dated
  pass counts belong in release evidence/release notes, not this evergreen document.)
- 320 real trials writing to the actual Windows clipboard: lower/medium-risk personal
  information was automatically protected and verified in every case that required it;
  higher-risk (NeedsDecision) values were correctly left for manual confirmation rather
  than silently auto-protected.
- Real Windows + real ChatGPT Windows Desktop clipboard-protection smoke test: passed,
  including copying content in another application before switching to ChatGPT.
- Real-world check that other, unsupported applications — including ChatGPT in a web
  browser — are not interfered with: passed.
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

- Not published as a tagged release — this remains only the current release candidate (see
  the note at the top of this document).

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
