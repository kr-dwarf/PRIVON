# Contributing to PRIVON

Thank you for your interest in PRIVON. This document describes how to report issues,
propose changes, and (eventually) submit code during the 0.1 public beta.

PRIVON is released under the [PolyForm Shield 1.0.0](./LICENSE) license — a
**source-available**, non-OSI license, not an open-source license. See `LICENSE` for the
exact, binding terms. This document describes project process and does not modify or
supersede anything in `LICENSE`.

This contribution policy is intentionally conservative for the 0.1 beta. It will evolve as
the project matures.

---

## 1. Issues are welcome

Bug reports, reproducible problem reports, and well-scoped feature discussions are welcome
via GitHub Issues. Please use the provided Issue forms.

**Security or privacy vulnerabilities must not be filed as a public Issue.** See
[SECURITY.md](./SECURITY.md).

---

## 2. Before writing code

- Open (or find) an Issue **first**, and discuss it there before writing any code.
- Wait for a maintainer to agree on the scope and approach.
- **Unsolicited feature PRs may be closed without review**, even if the code itself is
  good. This is not a judgment on quality — it's because scope and design need to be
  agreed on before implementation, not after.

---

## 3. External code PR policy (0.1 beta)

During the initial beta:

- Code PRs require **prior maintainer discussion and approval** (see §2). PRs opened
  without that discussion may be closed without review.
- Acceptance and merge are **not guaranteed**, even for approved-scope PRs.
- Large unsolicited refactors are not accepted.
- **One PR = one focused purpose.** Do not mix cleanup/refactoring with functional changes
  unless a maintainer has explicitly approved combining them.

---

## 4. Contribution rights

Because PRIVON is source-available and may be commercially and/or dual-licensed in the
future (see `LICENSE` and the project's public positioning), contribution rights are
handled carefully and conservatively:

- Submitting a pull request does **not** automatically transfer copyright in your
  contribution to the PRIVON project. You retain the rights you legally own in your
  contribution unless you separately and explicitly agree otherwise.
- Maintainers may require additional contribution terms (for example, a contributor
  license/rights agreement) before accepting substantial code contributions, particularly
  once such terms are finalized.
- **During the early 0.1 beta, maintainers may choose not to merge external code at all**
  until that contribution-rights policy is finalized, even if a PR is otherwise
  high-quality and in-scope. This is a deliberate, temporary caution — not a judgment on
  any individual contributor.

This section states project policy plainly and does not constitute a contributor license
agreement (CLA) or other legal instrument. No CLA currently exists for this project.

---

## 5. Technical requirements for any PR

If/when a code PR is accepted for review, it is expected to:

- Link the Issue it addresses.
- Explain **what** changed and **why**.
- Include a regression test for bug fixes where practical.
- Keep the full existing automated test suite GREEN (no regressions).
- Not weaken fail-closed behavior anywhere in the detection/protection pipeline.
- Not add any new telemetry or analytics.
- Not add any new network communication without explicit maintainer approval.
- Not add any new persistent logging or file writes without explicit maintainer review.
- Not add any new dependency without explicit maintainer approval.
- Contain **no real personal information (PII) and no real secrets of any kind.**
  **Use synthetic/fake data only** — in code, tests, comments, commit messages, and PR
  descriptions.

---

## 6. Security- and privacy-sensitive areas

Changes touching any of the following areas require heightened maintainer review, and are
especially unlikely to be merged without prior in-depth discussion:

- Detection (PII detection logic and rules)
- Policy (protection/decision policy)
- Windows clipboard interaction
- Self-write suppression / sequence handling / write verification
- Storage / DPAPI (local encrypted state)
- Windows session lock handling
- Diagnostics / logging
- Target/foreground authorization (which application PRIVON is permitted to act on)

---

## 7. AI-generated code

AI-assisted contributions are not prohibited. They receive **exactly the same** review and
testing requirements as any other contribution — no more, no less. The contributor
submitting a PR is responsible for understanding, validating, and standing behind the
correctness and safety of the code they submit, regardless of how it was produced.

---

## 8. Merge policy (intended, post-publication)

Once the repository is published on GitHub, the intended merge policy is:

- No direct pushes to `main`.
- Pull request review is required before merging.
- Required automated status checks will apply once CI is configured (not yet enabled).
- Squash merge is the preferred merge method for ordinary contributions.

**Branch protection is not yet configured.** It will be configured in the GitHub
repository settings after the repository is published — this document describes intent,
not current repository state.

---

## Questions

If anything in this document is unclear, please open an Issue to ask before starting work.
