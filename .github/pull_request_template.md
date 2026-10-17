<!--
  DRAFT — Worklane PR template (prepared by Scrum Master, 18 Oct 2026, Bangkok time).
  Target: .github/pull_request_template.md in BOTH arutdevs/Worklane-Api and arutdevs/Worklane-Web.
  Delete this comment block when copying into the repo.

  Title format:  WL-xx: <short imperative summary>        e.g.  WL-62: Block Admin from granting Owner
  Branch format: wl-xx/<short-kebab-description>          e.g.  wl-62/block-admin-owner-escalation
  Several small cards in the same files may share one PR:  WL-55 + WL-66: Show counts and assignee name on card
-->

## Card(s)
- WL-xx: <card title> (link to board card if available)
- Related API/Web PR (if any): <repo#number> — **merge order: API first, then Web**

## Risk level (pick one; reviewer depth depends on it)
- [ ] **A — High:** auth / roles / tenant isolation / secrets / migration / deletes data / payments-payroll / > 400 lines
- [ ] **B — Medium:** business logic, API contract, state handling
- [ ] **C — Low:** UI copy, i18n, styling, tests, docs only

Why this level (1 line):

## Summary
<!-- What changed and why, in 3–6 bullets. Link the AC from sprint-proposal.md. -->
-

**Points for อรุษ to look at (max 5):**
1.

## Acceptance criteria
<!-- Copy the AC numbers this PR covers and tick them. Mark anything not covered and why. -->
- [ ] AC 1 —
- [ ] AC 2 —
- Not covered in this PR (and where it will go):

## Verification
<!-- Required (engineering-standard.md §6). Real results from the commit in this PR only. QA treats a PR without this section as not ready for review. -->
Commit: <short sha> · Run at: <YYYY-MM-DD HH:mm> (Asia/Bangkok)

| Command | Result |
|---|---|
| API: `dotnet build Worklane.slnx -c Release` / Web: `npx ng build` | |
| API: `dotnet test Worklane.slnx -c Release` / Web: `npx ng test --watch=false` | N passed, N failed, N skipped (was N) |
| API: `dotnet list package --vulnerable --include-transitive` / Web: `npm audit --omit=dev --audit-level=moderate` | |
| CI `build-and-test` | <link> |

Bug fixes only. Failing before the fix (verbatim):
    <paste>
Passing after the fix (verbatim):
    <paste>

Tenant isolation (API): <test name> returns 404 for a non-member / 403 for a low role.
i18n (Web): en.json and th.json key parity spec passes.

Not verified: <what and why, or "none">

## Test evidence
- [ ] Unit / integration tests added or updated (list the main ones):
- [ ] API: new/changed endpoints have `[RequireWorkspaceRole]` (or equivalent) **and** a cross-tenant test (another workspace gets 403/404)
- [ ] Web: Vitest specs pass; no raw i18n keys when switching language
- Manual test steps and result:

## Screenshots (UI changes only)
<!-- Required for every visible UI change. Show the same screen in all four combinations. -->
| | Light | Dark |
|---|---|---|
| **ไทย (Thai)** | | |
| **English** | | |

- [ ] Dates show the year in **พ.ศ. when Thai** and **ค.ศ. when English** (API/DB values stay ISO, Gregorian)
- [ ] Keyboard / screen-reader check done where relevant (focus, aria labels)

## Migration notes
- [ ] No migration
- [ ] Migration included: `<MigrationName>` (only one migration per PR)
  - What it changes:
  - Backward compatible? Data backfill?
  - Rollback plan / `Down()` tested:
  - Backup taken before applying on shared DB? (dogfood board)

## Config / secrets / deploy
- [ ] No config change
- [ ] New/changed config keys (no secrets in the repo; values go to the secret store):

## Checklist
- [ ] Title and branch contain the card key (`WL-xx`)
- [ ] CI is green (build-and-test, npm-audit / nuget-audit, gitleaks when enabled)
- [ ] Pre-reviewed by: peer @____ · QA @____ · Security @____ (required for risk A) · SA @____ (optional for Backend)
- [ ] Size ≤ ~400 reviewed lines (excluding lockfiles, `*.Designer.cs`, snapshots, generated files) — **or** justification:
- [ ] No dependency upgrades mixed in (upgrades go in their own PR)
- [ ] en.json and th.json keys match (no hardcoded user-facing text)
- [ ] Opened as a **draft**; only อรุษ or the Tech Lead marks it ready (at most 3 PRs waiting for อรุษ at a time — check the queue first)
- [ ] `## Verification` is filled with real results (or `Not verified` says why)
