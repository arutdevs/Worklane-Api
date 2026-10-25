# Worklane

A multi-tenant project management SaaS (simplified Jira): workspaces and members, projects,
a Kanban board with drag-and-drop, and a dashboard that measures effort per person in man-days.

- `CLAUDE.md` — how to work in this repo, and the traps worth knowing first
- `PROGRESS.md` — what is built, what is missing, and what happened when

## Tech stack

- **Backend:** ASP.NET Core 10 Web API (C#), Clean Architecture (Domain / Application / Infrastructure / WebApi)
- **ORM:** Entity Framework Core + Npgsql (PostgreSQL)
- **Auth:** ASP.NET Core Identity + JWT access tokens (15 min) + rotating refresh tokens (7 days, hashed in DB)
- **Frontend:** Angular 22 (standalone components) + Bootstrap 5 + ng-bootstrap
- **i18n:** ngx-translate, English (`en`, default) and Thai (`th`)
- **Realtime:** SignalR hub at `/hubs/notifications`. Clients join a project group and receive board, comment, attachment, and presence events (see below)
- **Containers:** Docker Compose — `nginx` (API edge), `web` (public Web nginx), `api`, and `postgres`. The Angular app is a separate repository and is not built here. Local S3-compatible storage is a dev-only overlay (`compose.dev.yml`)

## Run everything

The full local stack (API image, Angular image from Worklane-Web, Postgres 16, and a one-shot migrator) is `./run-local.sh`. Steps, the URL, the demo login, and port clashes are in [RUN-LOCAL.md](RUN-LOCAL.md).

```bash
cp .env.example .env
# JWT signing key. .env is git-ignored. Compose will not start while JWT_SIGNING_KEY is empty.
printf 'JWT_SIGNING_KEY=%s\n' "$(openssl rand -base64 48 | tr -d '\n')" >> .env
docker compose up
```

`JWT_SIGNING_KEY` in `.env.example` is an empty placeholder. The `printf` line writes a generated key after it. Compose uses the shell environment before the env file, so export the same value if you prefer not to append: `export JWT_SIGNING_KEY="$(openssl rand -base64 48 | tr -d '\n')"`.

Fill `S3_ROOT_USER`, `S3_ROOT_PASSWORD`, `WL_S3_ACCESS_KEY`, and `WL_S3_SECRET_KEY` before the dev overlay (`openssl rand -hex 24` is enough for those). Then `docker compose -f docker-compose.yml -f compose.dev.yml up`.

`docker compose up` without `compose.dev.yml` starts the API edge nginx, the Web nginx, the API, and Postgres. It does not start the Angular app: this repository has no `./web` tree to build. The edge listens on **127.0.0.1:8080** and proxies to the API. The Web nginx is `web:8080` on the proxy network and forwards to that edge. `ng serve` stays on http://localhost:4200. The API allows both origins. That compose file is the one a staging or prod deploy should use. RustFS is not in it, and it does not start.

ห้าม deploy จนกว่า WL-63 (${JWT_SIGNING_KEY:?}) และ WL-71 จะเสร็จ

The deploy compose guard only selects files by name (`*compose*` and `*stack*`). A file named `prod.yml` would slip through. A symlinked `docker-compose.yml` also slips through, because the scan uses `find -type f` and does not follow symlinks. The real control is C7 in WL-71. Fixture changes under `.github/guard-fixtures/` go through Security review as a process. `.github/CODEOWNERS` names @arutdevs, the same account that opens every pull request, and code-owner review is not enforced, so that entry enforces nothing. Enforcing that review is อรุษ's ruleset decision.

The dev command above also starts local object storage. This builds and starts the stack:

| Service    | URL                                   |
|------------|----------------------------------------|
| nginx (API edge) | http://127.0.0.1:8080 — the only trusted proxy, see below |
| Web nginx | `web:8080` on the proxy network (172.30.20.3). Not published on the host. Forwards to the edge |
| Web (Angular, separate repository) | http://localhost:4200 via `ng serve`. This compose file does not start it |
| API direct (Swagger UI in Development) | http://127.0.0.1:5000/swagger (container port 8080). Forwarded headers are ignored |
| Postgres   | localhost:5432 (user/pass/db: `worklane`) |
| Object storage (S3 API, localhost only) | http://localhost:9000 |

`compose.dev.yml` sets `ASPNETCORE_ENVIRONMENT=Development`. `docker-compose.yml` does not, so a deploy of that file keeps the ASP.NET Production default. In Development, EF Core migrations run on API startup and a demo user, workspace, and project are seeded on first run.

**Demo login:** `demo@worklane.app` / `Demo123!`

## Local object storage

Dev compose runs RustFS as `s3` (S3 API published on `127.0.0.1:9000` only; the console port is not published) and a one-shot `s3-init` container. `s3-init` creates the private bucket `worklane-dev-files`, lifecycle rules for `quarantine/` (1 day) and `exports/` (7 days), and a least-privilege app user whose policy allows object operations on that bucket plus `s3:ListBucketMultipartUploads`. It checks each of those results and exits non-zero when a step fails. Every credential is read from `.env`. Compose has no default password: a missing value stops `docker compose` before the container starts. The built-in RustFS account name is rejected, and each password must be at least 16 characters.

`WL_S3_VERSIONING` defaults to `off`, matching Cloudflare R2 (the beta store). Set it to `on` in `.env` before the first `s3-init` run to exercise the DigitalOcean Spaces path: bucket versioning and a 20-day noncurrent-version expiry. Application code must not rely on versioning or bucket policy, because R2 provides neither. If versioning was enabled on the `s3data` volume, switching the flag to `off` leaves it in place; reset with `docker compose -f docker-compose.yml -f compose.dev.yml down -v`.

The API still stores attachments on the `attachments` volume. With the dev overlay, `FileStorage__S3__ServiceUrl` is `http://s3:9000` (and the other `FileStorage__S3__*` keys from the storage spec are set), but `FileStorageSettings` does not bind those keys yet, so `FileStorage__Provider` is left unset and the process keeps the local-disk provider. Images are pinned by digest in `compose.dev.yml`. Dependabot may open a pull request for a digest bump. Those pulls are not automerged. CI scans the pinned images with Trivy and fails on a CRITICAL finding that has a fix. It does not run RustFS as the test storage.

### Digest bump checklist

Keep the image as a literal `name:tag@sha256:…`. Do not wrap it in `${IMAGE:-…}`. A digest change is a reviewed edit, and it is not automerged. The pull request has to include:

- **Verify digest.** `docker buildx imagetools inspect <image>:<tag>`, and the index digest that will be pinned matches the digest in the compose file.
- **Provenance.** The provenance revision matches the git commit of that tag (`gh api repos/rustfs/rustfs/git/refs/tags/<tag>` for the server, `repos/rustfs/cli` for `rc`).
- **Advisories.** No new security advisory covers that version.
- **Smoke test.** `s3-init` exits 0, an anonymous object GET is 403, the app user is denied `CreateBucket`, and the app user can put, get, and delete an object.

## Golden path

1. Open the Angular app (`ng serve` on http://localhost:4200) → redirected to `/login`. This compose file does not start it. Login calls can go through nginx at http://127.0.0.1:8080, which is the trusted proxy below.
2. Register a new account (or use the demo login above) → lands in your personal workspace.
3. Go to **Projects** → create a project (name + key, e.g. `WL`). It starts with
   **To Do / In Progress / Done** columns.
4. Open the board → add cards, drag them between columns, give them an estimate in days.
   Entering **In Progress** starts the clock; reaching **Done** stops it.
5. Open **Dashboard** → estimated against actual man-days, per person.

   > Actual time is *elapsed weekdays*, not effort. Holding several cards at once bills the
   > full duration to each. Work that falls on Saturday or Sunday counts as **0 working days**
   > (WL-35 — this rule stays), and there is no holiday calendar. Read it as a trend.
6. Go to **Workspace settings** → invite a member by email + role.
   - If the invited email already has an account, they're added directly.
   - Otherwise a pending invitation is created. With Gmail SMTP configured the link is emailed;
     it is always logged to the API console as well. The app has an `/invitations/accept` page:
     a signed-in user accepts immediately, and a signed-out user is sent through login or
     register and then back to accept. `POST /api/invitations/accept { token }` is the same call.

## Board API added for the card detail

JSON is camelCase. Enums are strings. `UserRef` is `{ userId, displayName }`. Framework failures, including non-500 statuses, use a fixed sentence and do not return the exception text. Validation and other application exceptions keep the message they were thrown with.

| | |
|---|---|
| Activity | `GET /api/tasks/{taskId}/activity` → `TaskActivityDto[]`, newest first |
| Comments | `GET/POST /api/tasks/{taskId}/comments` (list is oldest first; create is 201) · `PATCH/DELETE /api/comments/{commentId}` (delete is 204) |
| Attachments | `GET/POST /api/tasks/{taskId}/attachments` (multipart field `file`, create is 201) · `GET /api/attachments/{attachmentId}/download` · `DELETE /api/attachments/{attachmentId}` (204) |
| Columns | create and `PATCH` accept `wipLimit` (`null` clears it; omitting it on PATCH leaves it). Column payloads include `wipLimit`, `taskCount`, `isOverWipLimit` |

`TaskActivityDto`: `{ id, taskId, type, actor: UserRef, createdAt, details }`. `type` is `Created`, `Moved`, `Updated`, `CommentAdded`, `CommentEdited`, `CommentDeleted`, `AttachmentAdded`, or `AttachmentDeleted`. A field edit writes **one `Updated` row per changed field**, with `details` `{ field, oldValue, newValue }`. A move's `details` are `{ fromColumnId, fromColumnName, toColumnId, toColumnName }`. Dragging a card inside the same column does not write a row. Changing `columnId` through `PATCH /api/tasks/{id}` is an `Updated` event; only `POST /api/tasks/{id}/move` writes `Moved` and moves the clock. The column must belong to the same project; a column from another project or workspace is **404** and its name is not written into activity. A `CommentAdded` row stores only `{ commentId }`. A `CommentEdited` row stores only `{ commentId, edited: true }`. Neither keeps the comment text. A data migration removes `body`, `oldValue`, and `newValue` from existing rows of both types; running it again is safe, and `Down` cannot restore the text. An assignee must already be a member of the workspace (**400** otherwise). Creating or deleting a comment or file broadcasts `TaskUpdated` with the card's new counts; editing a comment does not, because the counts are unchanged. A broadcast failure after the row is saved is logged and does not change the HTTP result. `JoinProject` requires a current workspace member and rejects an archived project. It checks membership again after joining the group. Removing a member drops that user's connections from every `project:{id}` in the workspace. That removal is in-memory and single-instance: another server process does not see those connections. There is no project-member removal API. The hub sets `CloseOnAuthenticationExpiration`.

`CommentDto`: `{ id, taskId, author: UserRef, body, createdAt, updatedAt }`. `updatedAt` is `null` until the author edits. Only the author can edit. A current member who wrote the comment, or a workspace admin or owner, can delete. A viewer cannot delete, including an author who has been demoted to viewer. Creating a comment uses the `comments` rate-limit policy (60 per minute). Uploads use `attachments` (30 per minute). The two buckets are separate, both partitioned by user subject and workspace. Over the window is **429** with `application/problem+json` and a `Retry-After` header. Login, register, and refresh share an `auth` limit per resolved client IP (`RateLimit:Auth`, 10 requests per 60 seconds unless configured otherwise). Over that window is **429** `rate_limited` with `scope` `ip` and a `Retry-After` header. The body has no `retryAfterSeconds`. Logout is not limited. Behind nginx the bucket is that client, not the proxy. See [Client IP behind nginx](#client-ip-behind-nginx).

`AttachmentDto`: `{ id, taskId, fileName, contentType, sizeBytes, uploadedBy: UserRef, createdAt }`. Files are stored through `IFileStorage` (local disk at `FileStorage:LocalPath`, default 10 MB per file). The extension allow-list and the leading bytes decide the type; the client `Content-Type` is ignored. Text uploads accept UTF-8, UTF-16 with a BOM, and TIS-620/windows-874 (Excel Thai). HTML and script content is still rejected. A configured `AllowedExtensions` or `AllowedContentTypes` list replaces the built-in default. The upload is streamed. Binary types are identified from the header. Text types (csv, txt, json) are scanned on every chunk, including a short overlap so a marker split between chunks is still rejected, without buffering the file twice. Over the per-file cap is **413**. Kestrel's global body limit is 2 MB. The upload route reads `FileStorageSettings.AttachmentRequestLimit` (`FileStorage:MaxBytes` plus 1 MB of multipart overhead; 11 MB when MaxBytes is the default 10 MB) on each request. A Content-Length above that ceiling is **413** `application/problem+json` with the fixed size-limit sentence, before the body is read. `FileStorage:MaxBytes` and `FileStorage:WorkspaceQuotaBytes` must be greater than 0 or the process refuses to start. A workspace whose stored bytes would pass the quota (the sum of `SizeBytes`) gets **409**. The check that allows the insert runs inside the same transaction as the insert, under `pg_advisory_xact_lock` keyed by the workspace, so two uploads cannot both pass a stale sum. A read before the file is stored only skips work when the workspace is already full. The `attachments` rate-limit policy returns **429**. A disallowed extension or a byte signature that does not match it is **415**, an empty file is **400**. Download is always `application/octet-stream` with `Content-Disposition: attachment`, `X-Content-Type-Options: nosniff`, and `Content-Security-Policy: default-src 'none'; sandbox`. Download and delete resolve the workspace from the attachment id. A current member who uploaded the file, or a workspace admin or owner, can delete. A viewer cannot, including an uploader who has been demoted to viewer. `docker compose` mounts the `attachments` volume at `/var/worklane/attachments`.

Every task payload (get, project list, my-tasks, cards inside a column, and create/move/update) includes `commentCount` and `attachmentCount`, filled by two grouped counts for the whole result set. Create and move responses are still the task object, plus `isOverWipLimit` for the destination column. The limit is soft: a full column still accepts the card. `isOverWipLimit` is true only when the card count is **greater** than `wipLimit` (sitting exactly at the limit is not over). The same flag is on every task payload, because they share one DTO.

SignalR group `project:{projectId}` (`JoinProject` / `LeaveProject`):

| Event | Payload |
|---|---|
| `TaskCreated` `TaskMoved` `TaskUpdated` | task |
| `CommentCreated` `CommentUpdated` | comment fields plus `projectId` |
| `CommentDeleted` | `{ projectId, taskId, commentId }` |
| `AttachmentAdded` | attachment fields plus `projectId` |
| `AttachmentDeleted` | `{ projectId, taskId, attachmentId }` |
| `UserOnline` `UserOffline` | user id |
| `PresenceSnapshot` | user ids, sent to the caller on join |
| `TaskEditingStarted` `TaskEditingStopped` | task id, user id |

## Multi-tenancy

Every workspace-scoped endpoint is guarded by a `[RequireWorkspaceRole(...)]` action filter that:

1. Resolves the `WorkspaceId` **from the route** (never trusts the request body), following the
   project back to its workspace for project-scoped routes like `/api/projects/{id}`.
2. Checks the caller is a member with at least the required role (`Viewer < Member < Admin < Owner`).
   A caller who is not a member gets the same 404 body as a missing id. A member whose role is too low gets 403 `workspace_access_denied`.
3. Pins an `ICurrentTenantService` scoped instance to that workspace, which EF Core's global query
   filters read — so even a query that forgets an explicit `WorkspaceId` predicate cannot leak
   cross-tenant rows.

## Running locally without Docker

**Backend:**

```bash
# Postgres must be reachable at the connection string in appsettings.json.
# Development ships no signing key. Set one with user-secrets, or export Jwt__Secret.
dotnet user-secrets set "Jwt:Secret" "$(openssl rand -base64 48 | tr -d '\n')" --project src/Worklane.WebApi
# export Jwt__Secret="$(openssl rand -base64 48 | tr -d '\n')"
dotnet run --project src/Worklane.WebApi
```

> On a machine where port 5432 is already taken, the container is published elsewhere and
> the connection string has to be overridden — see `CLAUDE.md` for the exact command.

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
├── web/                         # Angular app — separate git repository, ignored here
│   └── src/app/
│       ├── core/                # services, guards, interceptors, models
│       ├── shell/               # navbar, sidenav, shell layout
│       └── features/            # auth, workspaces, projects, board, dashboard
├── deploy/nginx/default.conf    # API edge; the only address ForwardedHeaders trusts
├── deploy/nginx/web.conf        # public Web nginx; forwards clients to the edge
├── compose.dev.yml              # local RustFS only; not used for staging/prod
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

The API refuses to start when `Jwt:Secret` (or any `Jwt:PreviousSecrets` entry) is missing, not valid base64, shorter than 32 bytes, or matches a signing key that was committed to this repository. The check compares SHA-256 fingerprints of the decoded key bytes and does not embed the plaintext. It runs in every environment, including Development.

`appsettings.Development.json` and `docker-compose.yml` do not contain a signing key. Compose sets `Jwt__Secret` from `${JWT_SIGNING_KEY:?}` and stops if that variable is empty. Put a generated value in `.env` (see above) or export it. For `dotnet run` in Development, user-secrets override the empty `Jwt:Secret`:

```bash
dotnet user-secrets set "Jwt:Secret" "$(openssl rand -base64 48 | tr -d '\n')" --project src/Worklane.WebApi
```

`openssl rand -base64 48` is 48 bytes; the minimum is 32. A process manager can set `Jwt__Secret` instead of user-secrets. `Jwt__PreviousSecrets__0` is an older key still accepted while its tokens expire. New tokens are signed only with `Jwt:Secret` and carry a `kid`.

Data Protection keys (Identity tokens such as password reset) use `SetApplicationName("Worklane")`. Set `DataProtection__KeysPath` to a directory that already exists and is writable. Production and any other non-Development, non-Testing environment refuse to start without it. Production uses `/var/lib/worklane/dataprotection-keys` on a persistent volume (the process runs as uid 1654 and the root filesystem is read-only). Development and Testing may omit the path: Development uses the framework default directory under the user profile, and Testing keeps an ephemeral key ring.

Object-storage credentials (`S3_ROOT_USER`, `S3_ROOT_PASSWORD`, `WL_S3_ACCESS_KEY`, `WL_S3_SECRET_KEY`) and `JWT_SIGNING_KEY` are not in the repo. Copy `.env.example` to `.env` and generate them locally. `.env` and `.env.*` are git-ignored; `.env.example` is the exception. Postgres, nginx, and the API publish on `127.0.0.1` only. This compose file does not publish the Angular app. `ng serve` uses http://localhost:4200, which the API allows, and so does http://localhost:8080.

## Client IP behind nginx

The API trusts one proxy. Compose pins the `nginx` service to **172.30.20.10** on the `proxy` network and publishes it on **127.0.0.1:8080**. That address is the only `KnownProxies` entry. The public Web nginx is the `web` service, pinned at **172.30.20.3** on the same network, reached as `web:8080`. It is not a trusted proxy. It forwards to the edge and sets `X-Forwarded-For` to the client it sees. The edge adopts that header only from **172.30.20.3**, then the API sees the edge at **172.30.20.10** and one client address. Pinning the Web nginx to **172.30.20.10** would take the edge's address, so it is routed through the edge instead. `KnownProxies` stays that single address.

The API is on `proxy` and on `backend`. Postgres is only on `backend`. Compose does not set `KnownNetworks`:

- `ForwardedHeaders__KnownProxies__0=172.30.20.10`

`172.30.20.0/24`, the gateway **172.30.20.1**, and the Web nginx **172.30.20.3** are not proxies. A direct connection from **172.30.20.3** to the API keeps the socket address, so a spoofed header is ignored. A `KnownNetworks` prefix wider than one host is rejected at startup. A direct connection to **127.0.0.1:5000** is not the edge. An empty `ForwardedHeaders:KnownProxies` and `KnownNetworks` keeps the framework default (loopback). Any configured proxy clears that default first, because an empty known-proxy list would trust every peer. `ForwardLimit` is 1. `UseForwardedHeaders` runs before the auth rate limiter.

Header contract, for the edge and for the Web nginx:

```nginx
proxy_set_header X-Forwarded-For $remote_addr;
```

Do not append `$proxy_add_x_forwarded_for` and do not copy or map `$http_x_forwarded_for`. The header is one address, the TCP peer of that nginx. A client-supplied `X-Forwarded-For` does not choose a bucket. If extra hops are forwarded anyway, only the rightmost address counts.

`CF-Connecting-IP` is the auth client only when the TCP peer is **172.30.20.10** and the value is a single IP. Both nginx configs send an empty `CF-Connecting-IP`, which nginx omits, so the inbound client header is not forwarded. Any other peer keeps the socket address. Two clients get two auth buckets. The 429 body stays `rate_limited` with `scope` `ip`. The wait is only the `Retry-After` header.
