# GitHub Publication Checklist

> **HISTORICAL / ARCHIVED.** This document records the repository's original
> **pre-publication** checklist, written before the PRIVON repository existed on GitHub.
> The repository has since been published, with real release tags (see
> [README.md](../README.md#release-provenance)/[README.ko.md](../README.ko.md#공개-기록)).
> Statements below such as "the repository does not exist on GitHub yet" describe that
> historical, pre-publication moment — not the current state. Unchecked (`[ ]`) items are
> historical intentions/checkpoints from that time, not a current to-do list; this file is
> preserved as-is rather than rewritten or re-checked.
>
> **This file is NOT evidence of current GitHub repository settings, branch protection,
> security settings, CI, or release status.** For the current state, use the current
> README, the actual repository configuration, and current release evidence — never this
> archived checklist.

This is a **manual UI settings checklist** for when the PRIVON repository is actually
published on GitHub. Nothing in this document asserts that any of these settings are
currently active — the repository does not exist on GitHub yet as of this document being
written. Each item should be verified/configured directly in the GitHub UI at publication
time, since exact option names, availability, and defaults can change and can depend on the
GitHub plan (free vs. paid) in use.

## 1. Repository visibility

- [ ] Repository is **Public**.

## 2. Default branch

- [ ] Default branch is named `main`.

## 3. Private Vulnerability Reporting

- [ ] **Enable** GitHub's Private Vulnerability Reporting (Settings → Security → Private
      vulnerability reporting).
- [ ] Confirm `SECURITY.md` in the repository root is detected and linked from the
      Security tab.
- [ ] *(Verify in current GitHub UI — availability/exact location has changed across
      GitHub plans and product iterations.)*

## 4. Branch protection / repository ruleset for `main`

- [ ] No direct pushes to `main` (require pull requests).
- [ ] Pull request required before merging.
- [ ] Maintainer/required-reviewer approval required before merging.
- [ ] Required status checks — **to be added once CI is configured** (not yet; do not
      enable "require status checks to pass" until real checks exist, or merging will be
      blocked with nothing to satisfy it).
- [ ] Consider branch deletion protection for `main` if the UI/plan offers it.
- [ ] *(Branch protection rules vs. the newer "repository rulesets" feature — verify
      which is available/recommended in the current GitHub UI and plan.)*

## 5. Merge method

- [ ] Prefer **squash merge** for ordinary contributions (per `CONTRIBUTING.md`).

## 6. Merge methods to disable/avoid

- [ ] Disable merge methods not intended for use (e.g. plain merge commits, rebase merge)
      unless a concrete reason to keep them arises later. Start narrow; widen only if
      needed.

## 7. Issues

- [ ] Issues are **enabled**.
- [ ] Confirm the Issue Forms in `.github/ISSUE_TEMPLATE/` (`bug_report.yml`,
      `feature_request.yml`, `config.yml`) render correctly in the GitHub "New issue"
      chooser after publication.

## 8. Discussions

- [ ] **Initially OFF**, unless a concrete need arises (e.g. community Q&A volume that
      doesn't fit Issues). Revisit later rather than enabling by default.

## 9. Wiki

- [ ] **Initially OFF**, unless a concrete need arises. `docs/` in-repo already serves as
      the technical documentation location.

## 10. Releases

- [ ] Use **GitHub Releases** to publish the packaged binary
      (`release/PRIVON-0.1.0-beta-win-x64.zip` + its `.sha256`), tagged e.g. `v0.1.0-beta`.
- [ ] README content (or a summary of it) is suitable as release notes / landing content
      for the release page.

## 11. GitHub Actions / CI permissions

- [ ] CI is **not yet added** as of this checklist.
- [ ] When CI is added later, grant it **no broader permissions than required** for the
      specific jobs it runs (e.g. avoid default `write` permissions on `GITHUB_TOKEN` if
      the workflow only needs to build/test; avoid granting access to secrets that a given
      workflow does not need).
- [ ] *(Verify current default `GITHUB_TOKEN` permission settings in the GitHub UI/plan
      before enabling any workflow — defaults have changed over time.)*

---

## Notes

- This checklist intentionally does not claim that every listed setting is available on
  every GitHub plan (free vs. Team/Enterprise). Items marked "verify in current GitHub UI"
  should be re-checked against whatever plan/UI is actually in use at publication time.
- This document should be updated (not just executed once) if the intended policy changes.
