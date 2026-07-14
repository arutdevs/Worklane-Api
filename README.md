# Worklane

A multi-tenant project management SaaS (simplified Jira). This is **Phase 1: foundation** —
authentication, workspaces, members/invitations, and projects. Boards and tasks come in later phases.

## Tech stack

- **Backend:** ASP.NET Core 10 Web API (C#), Clean Architecture (Domain / Application / Infrastructure / WebApi)
- **ORM:** Entity Framework Core + Npgsql (PostgreSQL)
- **Auth:** ASP.NET Core Identity + JWT access tokens (15 min) + rotating refresh tokens (7 days, hashed in DB)
- **Frontend:** Angular 22 (standalone components) + Bootstrap 5 + ng-bootstrap
- **i18n:** ngx-translate, English (`en`, default) and Thai (`th`)
- **Realtime:** SignalR hub wired up (`/hubs/notifications`), no events yet — Phase 2+
- **Containers:** Docker Compose — `api`, `web`, `postgres`

## Run everything

```bash
docker compose up
```

This builds and starts all three services:

| Service    | URL                                   |
|------------|----------------------------------------|
| Web (Angular, served by nginx) | http://localhost:4200        |
| API (Swagger UI in Development) | http://localhost:5000/swagger |
| Postgres   | localhost:5432 (user/pass/db: `worklane`) |

EF Core migrations are applied automatically on API startup in the `Development` environment
(the default in `docker-compose.yml`), and a demo user/workspace/project are seeded on first run.

**Demo login:** `demo@worklane.app` / `Demo123!`

## Golden path

1. Open http://localhost:4200 → redirected to `/login`.
2. Register a new account (or use the demo login above) → lands in your personal workspace.
3. Go to **Projects** → create a project (name + key, e.g. `WL`).
4. Go to **Workspace settings** → invite a member by email + role.
   - If the invited email already has an account, they're added directly.
   - Otherwise a pending invitation is created and the invite link is **logged to the API console**
     (no email sending in Phase 1) — accept it via `POST /api/invitations/accept { token }`.

## Multi-tenancy

Every workspace-scoped endpoint is guarded by a `[RequireWorkspaceRole(...)]` action filter that:

1. Resolves the `WorkspaceId` **from the route** (never trusts the request body), following the
   project back to its workspace for project-scoped routes like `/api/projects/{id}`.
2. Checks the caller is a member with at least the required role (`Viewer < Member < Admin < Owner`).
3. Pins an `ICurrentTenantService` scoped instance to that workspace, which EF Core's global query
   filters read — so even a query that forgets an explicit `WorkspaceId` predicate cannot leak
   cross-tenant rows.

## Running locally without Docker

**Backend:**

```bash
# Postgres must be reachable at the connection string in appsettings.Development.json
dotnet run --project src/Worklane.WebApi
```

**Frontend:**

```bash
cd web
npm install
npm start   # ng serve, proxies API calls to http://localhost:5000 per src/environments
```

## Tests

**Backend** — unit + integration (xUnit, `WebApplicationFactory`, Testcontainers for a real Postgres):

```bash
dotnet test
```

**Frontend** — unit tests (Vitest, via the Angular CLI):

```bash
cd web
npm test
```

## Project structure

```
worklane/
├── src/
│   ├── Worklane.Domain          # Entities, enums — no dependencies
│   ├── Worklane.Application     # DTOs, interfaces, FluentValidation validators
│   ├── Worklane.Infrastructure  # EF Core DbContext, Identity, migrations, service implementations
│   └── Worklane.WebApi          # Controllers, JWT auth, authorization filter, SignalR hub, Program.cs
├── tests/
│   └── Worklane.IntegrationTests
├── web/                         # Angular app
│   └── src/app/
│       ├── core/                # services, guards, interceptors, models
│       ├── shell/                # navbar, sidenav, shell layout
│       └── features/             # auth, workspaces, projects
└── docker-compose.yml
```

## Frontend theming

All visual styling is Bootstrap 5 utility classes + components (`ng-bootstrap` for modals/dropdowns).
Components do **not** carry their own stylesheets — the single theme definition lives in:

- `web/src/styles/_variables.scss` — Bootstrap Sass variable overrides (colors, fonts, radius)
- `web/src/styles.scss` — imports the variables, then Bootstrap, plus the few app-wide CSS custom
  properties (e.g. `--wl-sidebar-width`) referenced from templates for values Bootstrap has no
  utility class for

## Environment / secrets

`appsettings.Development.json` and `docker-compose.yml` ship a **development-only** JWT signing
secret for convenience. Replace `Jwt__Secret` with a real secret (e.g. via environment variable or
a secrets manager) before deploying anywhere real.
