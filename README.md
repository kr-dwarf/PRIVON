[English](./README.md) | [한국어](./README.ko.md)

---

# PRIVON

**PRIVON — turn on privacy protection before you use AI.**

> PRIVON 0.1 is a public beta. It is distributed for real-world validation and does not
> claim 1.0-level stability.

---

## What PRIVON 0.1 Is

PRIVON is a local-first privacy protection utility for Windows.

- If text you copy to the clipboard contains a supported personal-information pattern
  (such as a phone number or a Korean resident registration number), PRIVON detects and
  protects it locally, in the clipboard, **before** you manually paste it into ChatGPT.
- All processing happens on your own PC. Clipboard content is never sent to an external
  server.
- 0.1 is a public beta that prioritizes the **Korean (KR) usage environment** and the
  **clipboard path**.

---

## Quick Start (30 seconds)

No development tools or command line needed — just these steps.

1. Download `PRIVON-0.1.0-beta-win-x64.zip` from the GitHub Releases page.
2. Right-click the downloaded zip file → **Extract All**.
3. Double-click `PRIVON.exe` in the extracted folder to run it.
4. You'll know it's running correctly when the PRIVON icon appears in the notification
   area (system tray) at the bottom right of your screen.
5. Now use ChatGPT as usual. Whenever the ChatGPT window is the currently active
   (foreground) window and you copy (Ctrl+C) text that contains personal information,
   PRIVON automatically checks the clipboard and protects it, if needed, before you paste.

> PRIVON only acts after checking which window currently has focus (i.e., whether it's
> ChatGPT or not). While you're using any other program, it does not touch the clipboard
> at all.

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

> Note for English-speaking readers: in 0.1, the bracketed placeholder label itself
> (`[전화번호1]`, "phone number 1") is currently always in Korean, regardless of the
> language of the surrounding text you copy — this is current 0.1 behavior, not a
> translation gap in this README.

---

## 0.1 Supported Scope

- Windows 10 / 11 (64-bit, win-x64)
- Target application: ChatGPT
- Korean (KR) usage prioritized
- Clipboard path (clipboard-first) prioritized
- All processing performed locally
- No signup required

---

## Supported Personal Information Categories

0.1 currently attempts to detect and protect the following categories:

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
> [What PRIVON 0.1 Does Not Guarantee](#what-privon-01-does-not-guarantee) below.

---

## When Confirmation Is Needed (NeedsDecision)

For values judged to carry higher sensitivity, PRIVON does not replace them immediately.
Instead, it shows you a confirmation window and lets you decide.

In 0.1, this confirmation window offers exactly one action: **Protect All**. Clicking it
protects every item that needed a decision, then closes the window.

---

## What PRIVON 0.1 Does Not Guarantee

PRIVON 0.1 does not claim or guarantee any of the following:

- Full anonymization.
- Regulatory/privacy-law compliance certification.
- Detection of every possible identifier beyond the categories explicitly listed above.
- Interception or monitoring of ChatGPT's network traffic.
- Automatic sending of messages — PRIVON never sends anything on your behalf; you still
  paste and send manually.
- Protection for text typed directly into the composer (direct composer typing). 0.1's
  officially supported and guaranteed path is the **clipboard path**. Direct-typing
  protection exists internally only as a limited/experimental capability and is **not**
  part of the 0.1 protection guarantee.
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
  public 0.1 feature.

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

PRIVON 0.1.0-beta is not yet code-signed. Because of this, Windows may show a SmartScreen
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

## Beta Status & Feedback

PRIVON 0.1 is a **public beta** distributed for real-world validation. It does not yet
guarantee 1.0-level stability, and unexpected behavior may occur. If you find a problem,
please let us know via a GitHub Issue.

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

### Publishing a release build

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
install the .NET runtime to run it.

Repeatable release packaging (including ZIP + SHA-256 generation) is also available via
the `tools/publish-release.ps1` script:

```powershell
.\tools\publish-release.ps1
```

---

## Verification Status

- 1,667 automated tests passing (GREEN).
- Real Windows + real ChatGPT clipboard-protection smoke test: passed.
- Windows session lock (Win+L) behavior smoke test: passed.
- Release publish artifact launch smoke test: passed.

This verification reflects results within the tested scope and conditions — it does not
mean "works perfectly in every environment."

---

## Release provenance

<!-- FIRST_PUBLIC_RELEASE_TIMESTAMP: 2026-08-22T18:24:41+09:00 -->

- First public source: August 22, 2026
- First public beta release: August 22, 2026 — `v0.1.0-beta`
- First public source commit: `98a5c2d`
- `v0.1.0-beta` tagged commit: `1380d44`
- Release ZIP SHA-256: `d1417848847f5f2140bbf619662973ef69c86dff65ecd45607db8acf7699281f`

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
