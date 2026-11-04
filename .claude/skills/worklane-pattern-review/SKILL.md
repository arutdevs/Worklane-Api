---
name: worklane-pattern-review
description: Pass/fail review a bot runs on its own Worklane API pull-request diff before handoff. Use before handing off any PR into develop. Checks branch and PR target, layer placement, naming, constructor-injected validators, RequireWorkspaceRole and tenant filters, error codes and order, migration Up/Down, feature-folder tests, and docs against AGENTS.md, docs/conventions-api.md, docs/api-error-codes.md, and the Projects slice.
---

# Pattern review

Run this on the diff of the pull request you are handing off. The rules below are the pass/fail form of `AGENTS.md`, `docs/conventions-api.md`, `docs/api-error-codes.md`, and the Projects slice those docs name. Leave the pull request unmerged.

Compare new code with `src/Worklane.WebApi/Controllers/ProjectsController.cs`, `src/Worklane.Infrastructure/Services/ProjectService.cs`, and the other Projects paths in `docs/conventions-api.md`. Board controllers are the wrong template (`[FromServices]` validators, SignalR, uploads).

## How to run

1. List the diff against `develop`:

```bash
git diff --name-only origin/develop...HEAD
git diff --unified=3 origin/develop...HEAD
```

2. Read `AGENTS.md` (Branches, Layout, Traps), `docs/conventions-api.md` (Folder layout through Do not use), and any `code` you are about to add in `docs/api-error-codes.md`.
3. Judge every item below against that diff. An item that does not apply is PASS, and the evidence says which files were absent (for example `no controller in the diff`).
4. Print one table. `Result` is `PASS` or `FAIL`. Every FAIL cites `path:line` in the new file, then the rule it breaks. A FAIL with no line is incomplete. Do not merge, and do not apply a migration to a database while you review.

| Item | Result | Evidence |
|---|---|---|
| Branch and PR | PASS or FAIL | |
| Layer placement | PASS or FAIL | |
| Naming | PASS or FAIL | |
| Validator wiring | PASS or FAIL | |
| RequireWorkspaceRole | PASS or FAIL | |
| Tenant query filters | PASS or FAIL | |
| Error codes | PASS or FAIL | |
| Error order | PASS or FAIL | |
| Migrations | PASS or FAIL | |
| Tests | PASS or FAIL | |
| Docs | PASS or FAIL | |
| Known traps | PASS or FAIL | |

## Checklist

### Branch and PR

PASS when the branch was cut from `develop`, the pull request targets `develop`, and it is still open. `AGENTS.md` (Branches): every ticket starts there, the author opens the pull request, and leaves it unmerged. `main` is the other long-lived branch, not the ticket target. FAIL a base of `main`, a branch cut from anywhere else, or a merged pull request.

### Layer placement

New types sit on the layer in `docs/conventions-api.md` (Folder layout). Copy the Projects files named in that doc.

| Piece | Path |
|---|---|
| Entity | `src/Worklane.Domain/Entities/Project.cs` |
| EF configuration | `src/Worklane.Infrastructure/Persistence/Configurations/ProjectConfiguration.cs` |
| DTOs | `src/Worklane.Application/Projects/Dtos/ProjectDtos.cs` |
| Interface | `src/Worklane.Application/Projects/Interfaces/IProjectService.cs` |
| Validators | `src/Worklane.Application/Projects/Validators/` |
| Service | `src/Worklane.Infrastructure/Services/ProjectService.cs` |
| Controller | `src/Worklane.WebApi/Controllers/ProjectsController.cs` |
| Feature test | `tests/Worklane.IntegrationTests/Projects/DefaultColumnLocaleTests.cs` |

FAIL a DTO, validator, or `I*Service` outside `src/Worklane.Application/<Area>/`, a service implementation outside `src/Worklane.Infrastructure/Services/`, or a controller outside `src/Worklane.WebApi/Controllers/`. FAIL a reference from `src/Worklane.Domain` to EF Core or ASP.NET (`src/Worklane.Domain` has no package references). FAIL MediatR, `IRequest`, or a command or query handler. Register a new service with one `AddScoped` in `src/Worklane.Infrastructure/DependencyInjection.cs` (`AddScoped<IProjectService, ProjectService>()`). Do not register validators one by one; `AddValidatorsFromAssembly` in `src/Worklane.Application/DependencyInjection.cs` is the only registration.

### Naming

PASS when new names match the Projects column in `docs/conventions-api.md` (Naming):

- Entity: class in `src/Worklane.Domain/Entities/` (`Project`). The card entity stays `TaskItem`.
- Enum: `src/Worklane.Domain/Enums/` (`WorkspaceRole`: `Viewer = 0`, `Member = 1`, `Admin = 2`, `Owner = 3`).
- Response: record `*Dto`. Request: `Create*Request` / `Update*Request` in the same `<Area>/Dtos` file (`ProjectDto`, `CreateProjectRequest`, `UpdateProjectRequest`).
- Interface: `I<Area>Service` in `<Area>/Interfaces`. Implementation: `<Area>Service`.
- Validator: one class per file, class name = request type + `Validator` (`CreateProjectRequestValidator`).

FAIL a new validator whose name drops `Request` (the existing drift is `CreateTaskValidator` and the two classes in `src/Worklane.Application/Board/Validators/CommentValidators.cs`; leave those files). A new resource type added to `src/Worklane.WebApi/Filters/WorkspaceResourceType.cs` also adds `GetWorkspaceIdFor…Async`, as `GetWorkspaceIdForProjectAsync` does.

### Validator wiring

PASS when a new action copies `ProjectsController`: `IValidator<T>` is a constructor parameter (`_createValidator`, `_updateValidator`), and `ValidateAndThrowAsync` is the first call inside the action. Create is the shape: `[RequireWorkspaceRole(WorkspaceRole.Member)]` on `POST workspaces/{id:guid}/projects`, then `await _createValidator.ValidateAndThrowAsync(request, cancellationToken)` before `CreateProjectAsync`.

`[RequireWorkspaceRole]` runs first, so a non-member receives 404 `not_found` and never 400 `validation_failed`. FAIL `[FromServices] IValidator<T>` on a new action (`TasksController`, `ColumnsController`, and `TaskCommentsController` already do this; leave them). FAIL a validation filter or middleware that runs before `[RequireWorkspaceRole]`. `ValidationException` is `validation_failed`. The `[ApiController]` model-state path is a different code, `invalid_request_body`.

### RequireWorkspaceRole

Two layers are required on a workspace-scoped action (`docs/conventions-api.md`, Tenancy).

PASS when the action has `[Authorize]` and `[RequireWorkspaceRole]`, and the workspace id comes from the route, never the JSON body. Projects: list and create use `workspaces/{id}` with `Viewer` and `Member`; update and archive pass `WorkspaceResourceType.Project` with `Member` and `Admin` (`ProjectsController`). Default resource is `WorkspaceResourceType.Workspace` and the default route parameter is `id`. Project, column, task, comment, and attachment routes pass the matching `WorkspaceResourceType` and the parameter name.

A non-member and a missing id both get 404 `not_found` with detail `Resource was not found.` A member whose role is too low gets 403 `workspace_access_denied`. `WorkspaceRole` compares in enum order: `Viewer < Member < Admin < Owner`.

FAIL a workspace id, project id, or role taken from the body for authorization. FAIL a hand-written membership check in a controller action. `src/Worklane.WebApi/Hubs/NotificationsHub.cs` (`JoinProject`) is the exception: it has no MVC route and checks membership itself. Routes that are not workspace-scoped omit the attribute: `AuthController` is `[AllowAnonymous]`; `GET /api/workspaces` and `POST /api/workspaces` are the caller's own list and create; `POST /api/invitations/accept` looks the invitation up by token.

### Tenant query filters

PASS when a new tenant entity has exactly one `HasQueryFilter`, and that call is in `src/Worklane.Infrastructure/Persistence/WorklaneDbContext.cs` (`OnModelCreating`). The `Project` filter is the shape: not archived, a workspace is pinned, and `WorkspaceId` matches. `ProjectConfiguration` does not add another filter; its comment says EF Core keeps the last call.

FAIL a second `HasQueryFilter` in an `IEntityTypeConfiguration`. FAIL a query with no workspace pinned that can return rows (no pin matches nothing). Archived rows are excluded on that filter when the product hides them (`Project`, `TaskItem`; comments, attachments, and activity follow the task).

FAIL `IgnoreQueryFilters()` with no explicit predicate. The predicate is `WorkspaceId` when the workspace is known. `ProjectService.CreateProjectAsync` is the shape: `IgnoreQueryFilters()` then `AnyAsync(p => p.WorkspaceId == workspaceId && p.Key == request.Key)`. Id-to-workspace resolution predicates on the route id, because the filter runs before a tenant is pinned (`GetWorkspaceIdForProjectAsync` in the same file). A comment is not a substitute for the predicate.

### Error codes

Catalog: `docs/api-error-codes.md`. Bodies are `application/problem+json`. `code` is snake_case. `detail` is English. Counts and sizes are camelCase numbers.

PASS when every new `code`, and every new `detail` for an existing `code`, is a row in that catalog in the same diff, and the thrown `detail` matches the row, placeholders included. Status comes from the subclass in `src/Worklane.Application/Common/Exceptions/`: `BadRequestException` (400), `AuthenticationFailedException` (401), `ForbiddenAccessException` (403), `NotFoundException` (404), `ConflictException` (409), `PayloadTooLargeException` (413), `UnsupportedMediaTypeException` (415).

New missing-resource throws use `new NotFoundException("Resource was not found.")`. FAIL `NotFoundException(entityName, key)` in new code (`ProjectService` still uses it; do not copy those lines). FAIL `throw new ConflictException("some sentence")` when the catalog has a specific code. The one-argument constructor sets code `conflict`.

Several codes for one feature are constants next to the feature, matching the catalog word for word (`src/Worklane.Application/Board/ParentTaskErrors.cs`). A single shared unique conflict lives on `UniqueConstraintConflicts` (`ProjectKeyTaken()` → `project_key_taken`). Extensions go through `ErrorParams.Of` and must not use `type`, `title`, `status`, `detail`, `instance`, `code`, `errors`, `fieldCodes`, `traceId`. `fieldCodes` keys stay PascalCase. A `Must(...)` rule sets `.WithErrorCode(...)` itself; `NotEmpty()` becomes `not_empty` in `ExceptionHandlingMiddleware.ToFieldCode`.

A new unique index that can fire on a double submit is mapped in `src/Worklane.Infrastructure/Persistence/UniqueConstraintConflicts.cs`, or handled before `SaveChanges` with the same code. Parent-link SQLSTATE values go through `src/Worklane.Infrastructure/Persistence/ParentLinkConflicts.cs`. An unmapped unique violation must not surface as 500.

### Error order

PASS when a new endpoint still follows this order, first match wins (`docs/conventions-api.md`, Errors; pipeline in `src/Worklane.WebApi/Program.cs`):

1. Authentication. A missing or invalid JWT is 401 with an empty body. `RequireWorkspaceRole` can return 401 `unauthorized` with detail `A valid access token is required.` when the user id is empty.
2. Rate limiter (`UseRateLimiter`), before the action. 429 `rate_limited`, `scope` `user` or `ip`, and a `Retry-After` header. The body has no limit numbers. On `POST /api/workspaces` the order is 401, then 429, then 400, then 409.
3. `ContentLengthLimitMiddleware`. `Content-Length` over the limit is 413 `request_too_large` with no `maxBytes`. `file_too_large` is later and does include `maxBytes`.
4. `[ApiController]` model state. An unbindable body is 400 `invalid_request_body`. A body-limit exception stored in model state is 413 `request_too_large`.
5. `[RequireWorkspaceRole]`: 400 `invalid_route_id`, 404 `not_found` (missing id or non-member, same body), 403 `workspace_access_denied`.
6. `ValidateAndThrowAsync`: 400 `validation_failed`, plus `errors` and `fieldCodes`.
7. The service throws an `AppException` whose `code` and `detail` match a catalog row.
8. A Postgres unique violation or parent-link failure that escaped the service is mapped to the catalog 409. It must not surface as 500.
9. Anything else is 500 `internal_error` with `traceId` and detail `An unexpected error occurred.`

FAIL a new middleware path that returns `exception.Message`. `src/Worklane.WebApi/Middleware/ExceptionHandlingMiddleware.cs` already chooses the client sentence.

### Migrations

PASS when the diff has no migration, or the ticket is a schema change and the diff adds exactly one migration under `src/Worklane.Infrastructure/Persistence/Migrations/` (`docs/conventions-api.md`, Review checklist). The matching `.Designer.cs` and `WorklaneDbContextModelSnapshot.cs` ride along with that one migration; a second migration class is a FAIL.

That migration has both `Up` and `Down`. `Down` reverses `Up` in opposite order. The shape is `src/Worklane.Infrastructure/Persistence/Migrations/20261103122429_AddTaskParent.cs`: `Up` adds the column, unique constraint, index, foreign key, and check; `Down` drops the check, foreign key, index, unique constraint, then the column.

Review the C# only. Do not run the migration against a database. FAIL `dotnet ef database update`, a connection string pointed at the new migration, or starting the API so startup applies it. Development already applies migrations on startup in `src/Worklane.WebApi/Program.cs`; that path is not a review step.

### Tests

PASS when new tests sit in the feature folder `tests/Worklane.IntegrationTests/<Feature>/`. Projects uses `tests/Worklane.IntegrationTests/Projects/DefaultColumnLocaleTests.cs`. HTTP pipeline behaviour stays in `tests/Worklane.IntegrationTests/Web/`. Host and startup checks stay in `Infrastructure/`. A pure function can sit in `Domain/`.

Name the file for the behaviour. FAIL a new file under `Fixes/` or `Regression/`, or a class named for the review round. Add the next case to the feature file, or add a behaviour-named file beside it.

A workspace-scoped change covers a non-member (404 `not_found`, same body as a missing id) and a role that is too low (403 `workspace_access_denied`). `tests/Worklane.IntegrationTests/Web/RequireWorkspaceRoleFilterTests.cs` is the existing shape. `ReadFromJsonAsync` uses `TestAuthHelper.JsonOptions` (`tests/Worklane.IntegrationTests/Infrastructure/TestAuthHelper.cs`).

### Docs

PASS when a new or changed `code` or `detail` is in `docs/api-error-codes.md` in this diff, the same words as the throw. Work that should show on the board keeps `PROGRESS.md` current (`AGENTS.md`, Working agreement). FAIL a `code` or `detail` that exists only in C#. `docs/conventions-api.md` describes the target for new work; do not rewrite an existing controller just to match it.

### Known traps

PASS when the diff does not trip these. FAIL the line that does.

- A new enum column whose `0` member is a real value sets `HasSentinel` outside the enum. `TaskPriority.Low` is 0; `src/Worklane.Infrastructure/Persistence/Configurations/TaskItemConfiguration.cs` uses `.HasSentinel((TaskPriority)(-1))`.
- `INSERT … RETURNING` is materialized with `ToListAsync()` and the column aliased `AS "Value"`. `NextTaskNumberAsync` in `src/Worklane.Infrastructure/Services/BoardService.Parents.cs` does this. FAIL `FirstAsync()` or any further LINQ operator on that `SqlQuery`.
- Card order stays a `double`. FAIL an integer position. `BoardService.CalculateOrder` is the midpoint rule (`src/Worklane.Infrastructure/Services/BoardService.cs`).
- Timestamp asserts use `BeCloseTo(..., TimeSpan.FromMilliseconds(1))`, as `tests/Worklane.IntegrationTests/Board/TaskCommentTests.cs` does.
