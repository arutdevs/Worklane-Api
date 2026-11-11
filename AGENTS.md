# Worklane API

Multi-tenant project management API (workspaces, projects, a Kanban board, man-day reports). ASP.NET Core 10, EF Core + Npgsql, Identity + JWT.

Read `docs/conventions-api.md` before adding an endpoint. Error catalog: `docs/api-error-codes.md`. What is built: `PROGRESS.md`. How to run the stack: `README.md` and `RUN-LOCAL.md`.

## Project profile

### Branches

- `develop` is the integration branch. Every ticket starts as a branch from `develop` and opens a pull request into `develop`.
- `main` is the other long-lived branch. Push and pull-request workflows run on both.
- The ticket author opens the pull request and leaves it unmerged.

### Build and test

Solution file: `Worklane.slnx` (SDK-style; there is no `.sln`). Target framework `net10.0`.

| Project | Path |
|---|---|
| Domain | `src/Worklane.Domain/Worklane.Domain.csproj` |
| Application | `src/Worklane.Application/Worklane.Application.csproj` |
| Infrastructure | `src/Worklane.Infrastructure/Worklane.Infrastructure.csproj` |
| WebApi | `src/Worklane.WebApi/Worklane.WebApi.csproj` |
| Integration tests | `tests/Worklane.IntegrationTests/Worklane.IntegrationTests.csproj` |

`.github/workflows/backend.yml` runs:

```bash
dotnet restore Worklane.slnx
dotnet build Worklane.slnx --no-restore --configuration Release
dotnet test Worklane.slnx --no-build --configuration Release
```

From a clean tree, `dotnet build Worklane.slnx --configuration Release` and `dotnet test Worklane.slnx --configuration Release` are the same steps with restore included. Tests boot Postgres `postgres:16-alpine` per fixture (`tests/Worklane.IntegrationTests/Infrastructure/WorklaneWebApplicationFactory.cs`), so Docker has to be running.

### CI

| Workflow | When | What |
|---|---|---|
| `.github/workflows/backend.yml` | push and pull request to `main` and `develop` | restore, Release build, Release test of `Worklane.slnx` |
| `.github/workflows/security.yml` | push to `main` and `develop`, pull request to any base, Mondays 02:00 UTC | `dotnet list package --vulnerable --include-transitive` (any advisory fails) and gitleaks (`.gitleaks.toml`, full history) |
| `.github/workflows/image-scan.yml` | push to `main` and `develop`, pull request to any base | `scripts/check-deploy-compose.sh` and Trivy on the pinned RustFS images (a CRITICAL finding that has a fix fails the job) |

These workflows run against this API repository. The Angular app is built in Worklane-Web.

## Layout

```
src/Worklane.Domain            entities, enums, pure functions — no project references
src/Worklane.Application       DTOs, service interfaces, FluentValidation validators
src/Worklane.Infrastructure    EF Core, Identity, migrations, service implementations
src/Worklane.WebApi            controllers, RequireWorkspaceRole, middleware, SignalR
tests/Worklane.IntegrationTests
```

Layer rules, naming, and the Projects slice to copy: `docs/conventions-api.md`.

The Angular app is the `Worklane-Web` repository. `.gitignore` ignores `web/`. This checkout has no `web/` tree, so `git status` here says nothing about the frontend. Commit that repo on its own.

## Local dev

Postgres runs in Docker. **Port 5432 is taken by a native PostgreSQL 18 install on this machine**, so the container is published on **5433** and the API needs the connection string overridden — `appsettings.json` still points at 5432.

```bash
docker start worklane-pg
```

```bash
ASPNETCORE_ENVIRONMENT=Development \
ConnectionStrings__Default="Host=localhost;Port=5433;Database=worklane;Username=worklane;Password=worklane" \
dotnet run --project src/Worklane.WebApi --no-launch-profile --urls http://localhost:5000
```

If the container is gone, recreate it against the existing volume so the data survives:

```bash
docker run -d --name worklane-pg -e POSTGRES_DB=worklane -e POSTGRES_USER=worklane -e POSTGRES_PASSWORD=worklane -p 5433:5432 -v worklane_pgdata:/var/lib/postgresql/data postgres:16-alpine
```

**Never use "Reset to factory defaults" in Docker's error dialog.** It deletes every volume, including `worklane_pgdata` — the whole database.

`docker-compose.yml` publishes the API on `127.0.0.1:5000` (container port 8080). The same file maps Postgres to host port 5432; on this machine that mapping is not used — the container is published on **5433**. `src/Worklane.WebApi/Properties/launchSettings.json` listens on `http://localhost:5009`. Development applies EF migrations on startup (`src/Worklane.WebApi/Program.cs`).

On Windows, a running `dotnet run` locks `Worklane.Infrastructure.dll` and `dotnet build` / `dotnet test` fail with `MSB3027`. Stop that process first. After an unclean Docker Desktop shutdown, `scripts/Repair-DockerSockets.ps1` renames the stuck socket directory.

## Traps already paid for

- **Enum default.** `TaskPriority.Low` is 0, so EF treats an explicit Low as unset and the database default wins. `src/Worklane.Infrastructure/Persistence/Configurations/TaskItemConfiguration.cs` sets `.HasSentinel((TaskPriority)(-1))`. Any new enum column with a default needs the same treatment.
- **`INSERT … RETURNING`.** `NextTaskNumberAsync` in `src/Worklane.Infrastructure/Services/BoardService.Parents.cs` materialises `SqlQuery<int>` with `ToListAsync()` and aliases the column `AS "Value"`. A later LINQ operator such as `FirstAsync()` composes SQL and throws at runtime.
- **Card order** is a `double`. `BoardService.CalculateOrder` uses the midpoint of the neighbours. A gap under `0.001` rebalances the column to `(i + 1) * 65536` (`src/Worklane.Infrastructure/Services/BoardService.cs`).
- **Task numbers** (`WL-14`) come from `INSERT … ON CONFLICT … RETURNING` against `ProjectTaskCounters` in that same `NextTaskNumberAsync`.
- **String enums in tests.** Pass `TestAuthHelper.JsonOptions` (`tests/Worklane.IntegrationTests/Infrastructure/TestAuthHelper.cs`) to `ReadFromJsonAsync`. The API registers `JsonStringEnumConverter` in `Program.cs`.
- **Timestamps.** Postgres stores microseconds. Assert with `BeCloseTo(..., TimeSpan.FromMilliseconds(1))`, as `tests/Worklane.IntegrationTests/Board/TaskCommentTests.cs` does.
- **Demo seed.** `src/Worklane.WebApi/Seed/DevSeeder.cs` keeps going when `demo@worklane.app` already exists. Each step looks for its own rows (user, workspace, project, columns, tasks).
- **Man-days** are elapsed weekdays. Saturday and Sunday count as 0. `src/Worklane.Domain/Common/WorkingDays.cs`. The README states the product rule in full.

## Working agreement

Work that should show up on the Worklane board becomes a card. `.claude/skills/worklane-board/SKILL.md` says how. Keep `PROGRESS.md` current with the cards.

Before handing off a pull request, run `.claude/skills/worklane-pattern-review/SKILL.md` on that pull request's diff. Leave the pull request unmerged.

Write reports to the product owner in Thai. Keep code, comments, and commit messages in English.
