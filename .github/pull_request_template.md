<!--
  Title format:  WL-xx: <short imperative summary>        e.g.  WL-62: Block Admin from granting Owner
  Branch format: wl-xx/<short-kebab-description>          e.g.  wl-62/block-admin-owner-escalation
  Several small cards in the same files may share one PR:  WL-55 + WL-66: Show counts and assignee name on card
-->

## Card(s)
- WL-xx: `<card title>` (link to board card if available)
- Related API/Web PR (if any): `<repo#number>` — **merge order: API first, then Web**

## Risk level (pick one; reviewer depth depends on it)
- [ ] **A — High:** auth / roles / tenant isolation / secrets / migration / deletes data / payments-payroll / > 400 lines
- [ ] **B — Medium:** business logic, API contract, state handling
- [ ] **C — Low:** UI copy, i18n, styling, tests, docs only

Why this level (1 line):

## Summary
<!-- What changed and why, in 3–6 bullets. Link the AC from the WL card. -->
-

**Points for อรุษ to look at (max 5):**

1. —

## Acceptance criteria
<!-- Copy the AC numbers this PR covers and tick them. Mark anything not covered and why. -->
- [ ] AC 1 —
- [ ] AC 2 —
- Not covered in this PR (and where it will go):

## Test evidence
- [ ] Unit / integration tests added or updated (`dotnet test`; xUnit in `tests/Worklane.IntegrationTests`; list the main ones):
- [ ] API: new/changed endpoints have `[RequireWorkspaceRole]` (or equivalent) **and** a cross-tenant test (another workspace gets 403/404)
- Manual test steps and result:
- CI run link:

## Screenshots (UI changes only)
<!-- No UI in this API repo. Thai/English × light/dark screenshots and the keyboard / screen-reader check belong on the Web PR. -->
- [ ] API/DB values stay ISO, Gregorian (year display in **พ.ศ. when Thai** and **ค.ศ. when English** is the Web PR's check)

## Migration notes
- [ ] No migration
- [ ] Migration included: `<MigrationName>` (only one EF Core migration per PR; `src/Worklane.Infrastructure/Persistence/Migrations`)
  - What it changes:
  - Backward compatible? Data backfill?
  - Rollback plan / `Down()` tested:
  - Backup taken before applying on shared DB? (dogfood board)

## Config / secrets / deploy
- [ ] No config change
- [ ] New/changed config keys (no secrets in the repo; values go to the secret store):

## Checklist
- [ ] Title and branch contain the card key (`WL-xx`)
- [ ] CI is green (build-and-test, nuget-audit)
- [ ] Pre-reviewed by: peer @____ · QA @____ · Security @____ (required for risk A) · SA @____ (optional for Backend)
- [ ] Size ≤ ~400 reviewed lines (excluding lockfiles, `*.Designer.cs`, snapshots, generated files) — **or** justification:
- [ ] No dependency upgrades mixed in (upgrades go in their own PR)
- [ ] Not a draft; ready for อรุษ (at most 3 PRs waiting for อรุษ at a time — check the queue first)
