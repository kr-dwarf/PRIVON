# Security Policy

PRIVON handles privacy- and security-sensitive workflows: it inspects clipboard content for
personal information and modifies clipboard state before a user pastes into ChatGPT. Because
of that, security and privacy issues in PRIVON deserve careful, private handling.

## Reporting a vulnerability

**Do not report an exploitable privacy or security vulnerability through a public GitHub
Issue** if a private reporting channel is available.

After this repository is published on GitHub, maintainers intend to enable **GitHub Private
Vulnerability Reporting** for this repository. Once that is configured, the private
reporting entry point will be available from this repository's **Security** tab, and will
be referenced from this document.

**Until GitHub Private Vulnerability Reporting is configured and confirmed enabled, no
public reporting email address is published here.** We are not inventing a contact address
in advance of that feature being active. If you believe you have found a serious issue
before private reporting is available, please check back here or watch the repository's
Security tab — the channel will be announced there once it exists.

## What to include in a report

To help us evaluate and reproduce a report efficiently, please include:

- The affected PRIVON version (from the release you downloaded, e.g. `0.1.0-beta`).
- Your Windows version (e.g. Windows 11 24H2).
- The affected workflow (e.g. clipboard detection, Windows session lock handling, target
  authorization, a specific detector category).
- Reproduction steps, using **synthetic/fake data only** (see below).
- Expected behavior vs. actual behavior.
- The privacy/security impact as you understand it.
- Whether raw sensitive data could cross the protection boundary PRIVON is intended to
  enforce (i.e., whether unprotected PII could reach the clipboard, ChatGPT, disk, or a log
  when it shouldn't).

## Do not include real sensitive data in a report

When demonstrating a vulnerability, **use synthetic/fake test data only**. Do not include,
attach, or paste, in any form (including "for illustration," redacted, or partially
masked):

- Real passwords
- Real API keys
- Real access tokens
- Real resident/national ID numbers
- Real payment card data
- Real bank account details
- Any real private customer information

This applies even if you believe the data is already public or low-risk. If your
reproduction requires a value in one of these categories, please construct an obviously
synthetic placeholder instead (for example, a made-up phone number or a well-known
test-only card number pattern) — the same categories PRIVON's own test suite uses.

**Please do not upload real secrets to us even if you believe redaction has made them
safe.** Redaction mistakes happen; the safest approach is to never include real secret
values in a report at all.

## About PRIVON diagnostic logs

PRIVON ships with diagnostics **off by default** (`PUBLIC_DIAGNOSTIC_DEFAULT = OFF`).
Diagnostics are metadata-only (they do not record raw clipboard text, detected values, or
protected replacement text) and can be opted into for troubleshooting via
`PRIVON_ENABLE_DIAGNOSTICS=1`.

If a maintainer ever requests a diagnostic log as part of investigating a report, it must
first be reviewed under this project's privacy rules before being shared further. Do not
proactively attach a diagnostic log to a public report; wait to be asked, and share it only
through the private reporting channel once available.
