# Worklane — guide for agents

Multi-tenant project management SaaS (a simplified Jira). Read this before touching
anything; it records the traps that have already cost time on this codebase.

`PROGRESS.md` is the companion document — it says what is built, what is missing, and
what happened when. This file says how to work here.

---

## Layout

```
worklane/
├── src/
│   ├── Worklane.Domain          entities, enums, pure logic — no dependencies
│   ├── Worklane.Application     DTOs, interfaces, FluentValidation validators
│   ├── Worklane.Infrastructure  EF Core, Identity, migrations, service implementations
│   └── Worklane.WebApi          controllers, auth filter, SignalR hub, Program.cs
├── tests/Worklane.IntegrationTests
├── web/                         Angular app — SEPARATE GIT REPOSITORY, see below
└── PROGRESS.md
```

**Backend:** ASP.NET Core 10, EF Core + Npgsql, Identity + JWT (15 min access, 7 day
rotating refresh hashed in DB).
**Frontend:** Angular 22 standalone, Bootstrap 5, ng-bootstrap, ngx-translate (en/th).

---

## ⚠️ `web/` is a different git repository

The root `.gitignore` ignores `web/`, because the frontend lives in its own repo
(`Worklane-Web`). **`git status` at the root shows nothing about the frontend, even when
it has weeks of uncommitted work.** This has already caused 31 files of finished work to
sit unbacked-up for five weeks.

Always check both:

```bash
git status && git -C web status
```

Commit each repo separately. A change that spans both needs a commit in each.

---

## Running it

Postgres runs in Docker. **Port 5432 is taken by a native PostgreSQL 18 install on this
machine**, so the container is published on **5433** and the API needs the connection
string overridden — `appsettings.json` still points at 5432.

```bash
docker start worklane-pg
```

```bash
ASPNETCORE_ENVIRONMENT=Development \
ConnectionStrings__Default="Host=localhost;Port=5433;Database=worklane;Username=worklane;Password=worklane" \
dotnet run --project src/Worklane.WebApi --no-launch-profile --urls http://localhost:5000
```

```bash
cd web && npm start
```

`--no-launch-profile --urls http://localhost:5000` is required: `launchSettings.json` says
port 5009, but `web/src/environments/environment.development.ts` calls `localhost:5000`.

| | |
|---|---|
| Web | http://localhost:4200 |
| Swagger | http://localhost:5000/swagger |
| Login | `demo@worklane.app` / `Demo123!` |

Migrations apply automatically on startup in Development.

If the container is gone, recreate it against the existing volume so the data survives:

```bash
docker run -d --name worklane-pg -e POSTGRES_DB=worklane -e POSTGRES_USER=worklane -e POSTGRES_PASSWORD=worklane -p 5433:5432 -v worklane_pgdata:/var/lib/postgresql/data postgres:16-alpine
```

---

## ⚠️ Docker Desktop will not start after an unclean shutdown

Shutting Windows down without quitting Docker Desktop leaves its Unix sockets behind as
reparse points. On the next start Docker tries to remove each one before listening on it,
fails with `The file cannot be accessed by the system`, and dies with "An unexpected error
occurred". This has happened repeatedly on this machine.

The sockets cannot be deleted — not by `Remove-Item`, `del`, or `fsutil reparsepoint
delete`. Renaming the directory holding them does work. Failures also surface one service
at a time, so clearing the Inference manager's socket only reveals the Secrets Engine's.

```bash
powershell -ExecutionPolicy Bypass -File scripts/Repair-DockerSockets.ps1
```

**Never use "Reset to factory defaults" in Docker's error dialog.** It deletes every
volume, including `worklane_pgdata` — the whole database.

---

## ⚠️ Stop the API before building

A running `dotnet run` holds `Worklane.Infrastructure.dll`, and `dotnet test`,
`dotnet build` and `dotnet ef` all fail with `MSB3027 … file is locked`. Kill it first:

```bash
powershell -c "(Get-NetTCPConnection -LocalPort 5000 -State Listen).OwningProcess | Stop-Process -Force"
```

```bash
dotnet test
```

Tests boot a real Postgres per fixture through Testcontainers, so Docker must be running.

Frontend:

```bash
cd web && npx ng test --watch=false
```

Run it through the Angular CLI, not `npx vitest` — the CLI supplies the vitest setup, and
calling vitest directly fails with `describe is not defined`. Shared DTO builders live in
`web/src/testing/board-fixtures.ts`; extend those rather than hand-rolling a task object in
each spec.

---

## Architecture rules

**Multi-tenancy is two layers, and both matter.** `[RequireWorkspaceRole(...)]` resolves
`WorkspaceId` **from the route only** — never from the request body — and pins
`ICurrentTenantService`. EF global query filters then read that, so a query that forgets an
explicit `WorkspaceId` predicate still cannot leak across tenants. When you add a
workspace-scoped endpoint, it needs the attribute; do not hand-roll the check.

**Fractional ordering.** Cards carry `Order` as a `double`; moving one takes the midpoint
of its neighbours and rebalances the column only when the gap falls under `0.001`. Do not
replace this with integer indexes.

**Task numbers** (`WL-14`) come from an atomic `INSERT … ON CONFLICT … RETURNING` against
`ProjectTaskCounters`. See the EF trap below before touching it.

**Work metrics.** `BoardColumn.IsInProgress` starts the clock and `IsDone` stops it —
`MoveTaskAsync` stamps `StartedAt` once (never restarting it) and sets or clears
`CompletedAt`. Every column change also writes a `TaskActivity` row, because the two
timestamps only hold the latest state. Man-day figures are **elapsed weekdays, not
effort**: someone holding three cards accrues full time on all three, and there is no
holiday calendar. Work that falls on Saturday or Sunday counts as **0 working days**
(WL-35 — this rule stays). Say so whenever you report these numbers.

**Services** are registered in `Worklane.Infrastructure/DependencyInjection.cs`. Follow the
existing shape: interface in `Application/<Area>/Interfaces`, implementation in
`Infrastructure/Services`, DTOs in `Application/<Area>/Dtos`.

---

## Traps that have already caused bugs

**EF `HasDefaultValue` on an enum whose first member is `0`.** `TaskPriority.Low = 0` is
the CLR default, so EF treated an explicit `Low` as "never set", dropped the column from
the INSERT, and let the database default (`Medium`) win — silently, with no error. Fixed
with `.HasSentinel((TaskPriority)(-1))` in `TaskItemConfiguration`. Any new enum column
with a default has the same hazard.

**`SqlQuery` over non-composable SQL.** `INSERT … RETURNING` cannot have a LINQ operator
on top of it — `FirstAsync()` composes a `LIMIT` and throws at runtime. Materialise with
`ToListAsync()` and alias the column `AS "Value"`, which is what `SqlQuery<T>` shapes from.

**String enums in tests.** The API serialises enums as strings. Always pass
`TestAuthHelper.JsonOptions` to `ReadFromJsonAsync<T>()`; without it deserialization throws
inside test setup, which reads as seven unrelated failures.

**Timestamp assertions.** Postgres stores microseconds, so a value read back is not
bit-identical to the one a write returned from the tracked entity. Use
`BeCloseTo(..., TimeSpan.FromMilliseconds(1))`, not `Be(...)`.

**`DevSeeder` returns early** if `demo@worklane.app` exists, so its board seeding never
runs on a machine that has been through Phase 1. A fresh clone gets a board; an existing
database does not.

**CI runs, but only on GitHub.** `.github/workflows/` builds and tests both repos on push
and pull request against `main` and `develop`, and audits dependencies weekly. That is no
substitute for running things locally before saying they work — a green pipeline arrives
minutes after the claim, and only for what was pushed.

---

## Frontend conventions

- Services are decorated `@Service()` from `@angular/core` and injected with `inject()`.
- **Components carry no stylesheets.** All styling is Bootstrap utilities plus the single
  theme in `web/src/styles.scss` and `web/src/styles/_variables.scss`. Add shared classes
  there (`wl-` prefix) rather than creating a component `.scss`.
- State is signals; derived values are `computed()`. Board filtering happens client-side
  for instant feedback.
- Every user-facing string goes through `ngx-translate`, in **both** `web/public/i18n/en.json`
  and `th.json`. The user works in Thai — untranslated strings are a real defect here.
- No charting library: the dashboard bars are plain CSS.

---

## Working agreement

**Every piece of work becomes a card on the `WL` board** — features, bug fixes, and
decisions that need making, without being asked. The user runs Worklane on itself and
reads that board to see where things stand; work that never became a card is invisible to
them. See `.claude/skills/worklane-board/SKILL.md` for how.

Keep `PROGRESS.md` current alongside the cards.

Write in the user's language (Thai) when reporting; keep code, comments and commit
messages in English.
