# API conventions

Copy the Projects slice. It is the smallest feature that crosses every layer and already follows the tenancy, validation, and error-code rules below. Board code is the reference for moves, comments, and parent links; it is the wrong template for a new CRUD feature (SignalR, uploads).

This file describes the target for new work. It does not change existing controllers.

## Folder layout

`src/Worklane.Domain` has no package references.

```
src/Worklane.Domain/
  Common/          IHasTimestamps, WorkingDays
  Entities/        one class per table (Project, TaskItem, BoardColumn, …)
  Enums/           WorkspaceRole, TaskPriority, …
src/Worklane.Application/
  <Area>/Dtos/          request and response records
  <Area>/Interfaces/    I<Area>Service
  <Area>/Validators/    one AbstractValidator per request
  Common/Exceptions/    AppException and the HTTP subclasses
  Common/Interfaces/    ICurrentTenantService, ICurrentUserService, IFileStorage
  Common/Options/       bound settings
  DependencyInjection.cs    AddValidatorsFromAssembly only
src/Worklane.Infrastructure/
  Persistence/WorklaneDbContext.cs          the only place that calls HasQueryFilter
  Persistence/Configurations/               IEntityTypeConfiguration<T>, one per entity
  Persistence/UniqueConstraintConflicts.cs  Postgres 23505 → 409 code
  Persistence/ParentLinkConflicts.cs        parent-link SQLSTATE → 409 code
  Persistence/Migrations/
  Services/                                 I<Area>Service implementations
  Multitenancy/CurrentTenantService.cs
  Identity/
  DependencyInjection.cs                    DbContext, Identity, one AddScoped per service
src/Worklane.WebApi/
  Controllers/
  Filters/RequireWorkspaceRoleAttribute.cs
  Middleware/ExceptionHandlingMiddleware.cs
  Hubs/NotificationsHub.cs                  no route filter; checks membership itself
  Program.cs
tests/Worklane.IntegrationTests/<Feature>/
```

Areas that exist today: `Auth`, `Board`, `Projects`, `Reports`, `Workspaces`. `Reports` has DTOs and `IReportService` and no validators (the action takes no body).

Register a new service in `src/Worklane.Infrastructure/DependencyInjection.cs` (`AddScoped<IProjectService, ProjectService>()`). A new `AbstractValidator<T>` is picked up by `AddValidatorsFromAssembly` in `src/Worklane.Application/DependencyInjection.cs`. Do not register validators one by one.

## Naming

There are no command or query types, and no MediatR handlers. A write is a request record plus a method on the service interface. A read is a service method that returns a DTO.

| Kind | Shape | Example |
|---|---|---|
| Entity | class in `Domain/Entities` | `Project` (`src/Worklane.Domain/Entities/Project.cs`). The card entity is `TaskItem`, because `Task` is the framework type. |
| Enum | `Domain/Enums` | `WorkspaceRole`: `Viewer = 0`, `Member = 1`, `Admin = 2`, `Owner = 3` |
| Response | record `*Dto` in `<Area>/Dtos` | `ProjectDto` in `src/Worklane.Application/Projects/Dtos/ProjectDtos.cs` |
| Request | record `Create*Request` / `Update*Request` in the same file | `CreateProjectRequest`, `UpdateProjectRequest` |
| Service interface | `I<Area>Service` in `<Area>/Interfaces` | `src/Worklane.Application/Projects/Interfaces/IProjectService.cs` |
| Service | `<Area>Service` in `Infrastructure/Services` | `src/Worklane.Infrastructure/Services/ProjectService.cs` |
| Validator | `<RequestType>Validator` in `<Area>/Validators` | `CreateProjectRequestValidator` |

Validator class names already drift. Board uses `CreateTaskValidator` for `CreateTaskRequest`, and `src/Worklane.Application/Board/Validators/CommentValidators.cs` holds two classes. New validators follow the Projects names: one class per file, class name = request type + `Validator`.

`GetWorkspaceIdForProjectAsync` (and the column, task, comment, and attachment equivalents) exists so `RequireWorkspaceRoleAttribute` can resolve a route id. Add one when a new resource type is added to `src/Worklane.WebApi/Filters/WorkspaceResourceType.cs`.

## Golden reference: Projects

Copy these files:

| Piece | Path |
|---|---|
| Entity | `src/Worklane.Domain/Entities/Project.cs` |
| EF configuration | `src/Worklane.Infrastructure/Persistence/Configurations/ProjectConfiguration.cs` |
| DTOs | `src/Worklane.Application/Projects/Dtos/ProjectDtos.cs` |
| Interface | `src/Worklane.Application/Projects/Interfaces/IProjectService.cs` |
| Validators | `src/Worklane.Application/Projects/Validators/CreateProjectRequestValidator.cs`, `UpdateProjectRequestValidator.cs` |
| Service | `src/Worklane.Infrastructure/Services/ProjectService.cs` |
| Controller | `src/Worklane.WebApi/Controllers/ProjectsController.cs` |
| Feature test | `tests/Worklane.IntegrationTests/Projects/DefaultColumnLocaleTests.cs` |

Why this slice:

- It crosses Domain, Application, Infrastructure, WebApi, and a feature-folder test, and it does not pull in SignalR or uploads.
- Every action has `[Authorize]` plus `[RequireWorkspaceRole]`. List and create take the workspace id from `workspaces/{id}`. Update and archive pass `WorkspaceResourceType.Project` so the filter looks the workspace up from the project id.
- Roles step with the work: `Viewer` to list, `Member` to create and update, `Admin` to archive.
- Validators are constructor-injected and `ValidateAndThrowAsync` runs before the service call. That is the target wiring below.
- Reads use the global query filter. `IgnoreQueryFilters` appears only on the archived-key check (the unique index includes archived rows, which the filter hides) and on `GetWorkspaceIdForProjectAsync` (the filter calls this before a tenant is pinned). Both keep an explicit predicate.
- A taken key throws `UniqueConstraintConflicts.ProjectKeyTaken()` (`project_key_taken` in `docs/api-error-codes.md`).

Two details in this slice are not the target. `CreateProject` returns `Ok` (200). `TasksController.CreateTask` returns `CreatedAtAction` (201), and register returns 201. `UpdateProjectAsync` throws `NotFoundException(nameof(Project), projectId)`, whose detail is `Entity "Project" ({id}) was not found.` The catalog sentence for `not_found` is `Resource was not found.` See [Errors](#errors).

The Projects test covers default column names, not the role matrix. Cross-tenant 404 vs 403 lives in `tests/Worklane.IntegrationTests/Web/RequireWorkspaceRoleFilterTests.cs`.

## Templates

Controller (`ProjectsController` is the full file):

```csharp
[ApiController]
[Route("api")]
[Authorize]
public class ProjectsController : ControllerBase
{
    private readonly IProjectService _projectService;
    private readonly IValidator<CreateProjectRequest> _createValidator;

    [HttpPost("workspaces/{id:guid}/projects")]
    [RequireWorkspaceRole(WorkspaceRole.Member)]
    public async Task<ActionResult<ProjectDto>> CreateProject(
        Guid id, CreateProjectRequest request, CancellationToken cancellationToken)
    {
        await _createValidator.ValidateAndThrowAsync(request, cancellationToken);
        var project = await _projectService.CreateProjectAsync(
            id, /* user id */, request, /* Accept-Language */, cancellationToken);
        return Ok(project);
    }
}
```

Service. Duplicate key, from `ProjectService.CreateProjectAsync` (the filter hides archived rows, and the unique index still includes them):

```csharp
var keyTaken = await _db.Projects
    .IgnoreQueryFilters()
    .AnyAsync(p => p.WorkspaceId == workspaceId && p.Key == request.Key, cancellationToken);
if (keyTaken)
    throw UniqueConstraintConflicts.ProjectKeyTaken();
```

Missing row. New code uses the catalog sentence. The load itself stays on the query filter, as `UpdateProjectAsync` does:

```csharp
var project = await _db.Projects.SingleOrDefaultAsync(p => p.Id == projectId, cancellationToken);
if (project is null)
    throw new NotFoundException("Resource was not found.");
```

Validator (`CreateProjectRequestValidator`):

```csharp
public class CreateProjectRequestValidator : AbstractValidator<CreateProjectRequest>
{
    public CreateProjectRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Key).NotEmpty().MaximumLength(10).Matches("^[A-Z][A-Z0-9]*$");
    }
}
```

`NotEmpty()` becomes field code `not_empty` in `ExceptionHandlingMiddleware.ToFieldCode` (it strips the `Validator` suffix). A `Must(...)` rule does not, so set the code yourself, as `CreateTaskValidator` does with `.WithErrorCode("not_empty")`. `fieldCodes` keys are the PascalCase property names. That is current behaviour; `docs/api-error-codes.md` records it.

Error code. Throw an `AppException` subclass. `Code` is the catalog `code`. The message is that row's `detail`, with the same placeholders. Extensions go through `ErrorParams.Of` and must not use the reserved names (`type`, `title`, `status`, `detail`, `instance`, `code`, `errors`, `fieldCodes`, `traceId`).

```csharp
throw UniqueConstraintConflicts.ProjectKeyTaken();

throw new ConflictException(
    ParentTaskErrors.ParentChildLimit,
    string.Format(CultureInfo.InvariantCulture, ParentTaskErrors.ParentChildLimitDetail, max),
    ErrorParams.Of((ParentTaskErrors.MaxChildren, max)));

throw new BadRequestException("assignee_not_member", "Assignee must be a member of this workspace.");
```

`ParentTaskErrors` (`src/Worklane.Application/Board/ParentTaskErrors.cs`) is the pattern when one feature has several codes: constants for the code and the English detail, next to the feature, matching the catalog word for word. A single shared conflict can live on `UniqueConstraintConflicts` instead. Add the row to `docs/api-error-codes.md` in the same change. The Web app localizes from `code`; the API does not.

Subclass by status: `BadRequestException` (400), `AuthenticationFailedException` (401), `ForbiddenAccessException` (403), `NotFoundException` (404), `ConflictException` (409), `PayloadTooLargeException` (413), `UnsupportedMediaTypeException` (415). Each lives in `src/Worklane.Application/Common/Exceptions/`.

## Validators (target)

Registration is already one line: `AddValidatorsFromAssembly` in `src/Worklane.Application/DependencyInjection.cs`.

Controllers take `IValidator<T>` in the constructor and call `ValidateAndThrowAsync` as the first line of the action: `ProjectsController`, `WorkspacesController`, `AuthController`, `InvitationsController`, `TasksController`, `ColumnsController`, `TaskCommentsController`.

The call stays inside the action on purpose. `[RequireWorkspaceRole]` runs first, so a non-member receives 404 `not_found` and never a 400 `validation_failed`. `ValidationException` is what `ExceptionHandlingMiddleware` turns into `validation_failed` with `errors` and `fieldCodes`. The `[ApiController]` model-state path is a different code, `invalid_request_body`.

## Tenancy

Two layers, both required on a workspace-scoped action.

1. `[RequireWorkspaceRole(minimumRole, resourceType, routeParam)]` (`src/Worklane.WebApi/Filters/RequireWorkspaceRoleAttribute.cs`). The workspace id comes from the route, never from the body. Default resource is `WorkspaceResourceType.Workspace` and the default route parameter is `id`. Project, column, task, comment, and attachment routes pass the matching `WorkspaceResourceType` and the parameter name (`projectId`, `taskId`, …).
2. On success the filter calls `ICurrentTenantService.SetWorkspace`. `WorklaneDbContext.OnModelCreating` is the only `HasQueryFilter` site. EF Core keeps the last filter per entity, which is why `ProjectConfiguration` does not add another. A query with no workspace pinned matches nothing. `Project` and `TaskItem` filters also exclude archived rows; comments, attachments, and activity follow the task.

`IgnoreQueryFilters` is for work that runs before a tenant is pinned (membership lookup, "my workspaces", invitation accept, id-to-workspace resolution) or that must see rows the filter hides (archived project keys, archived cards during column delete). Keep an explicit `WorkspaceId` predicate on that query. The DbContext comment states the same rule.

`WorkspaceRole` compares in enum order: `Viewer < Member < Admin < Owner`. A non-member and a missing id both get 404 `not_found` with detail `Resource was not found.` A member whose role is too low gets 403 `workspace_access_denied`.

Routes that are not workspace-scoped omit the attribute: `AuthController` is `[AllowAnonymous]`; `GET /api/workspaces` and `POST /api/workspaces` are the caller's own list and create; `POST /api/invitations/accept` looks the invitation up by token with no tenant pinned. `NotificationsHub.JoinProject` cannot use the attribute (there is no MVC route). It checks membership itself. Controller actions use the attribute.

## Errors

Catalog: `docs/api-error-codes.md`. Bodies are `application/problem+json`. `code` is snake_case. Counts and sizes are camelCase numbers. `detail` is English.

Pipeline order in `src/Worklane.WebApi/Program.cs`, first match wins:

1. Authentication. A missing or invalid JWT is 401 with an empty body. That empty body is the bearer challenge. `RequireWorkspaceRole` can also return 401 `unauthorized` with detail `A valid access token is required.` when the user id is empty.
2. Rate limiter (`UseRateLimiter`), still before the action. 429 `rate_limited`, `scope` `user` or `ip`, and a `Retry-After` header. The body has no limit numbers. On `POST /api/workspaces` the catalog order is 401, then 429, then 400, then 409.
3. `ContentLengthLimitMiddleware`. A `Content-Length` over the limit is 413 `request_too_large` with the fixed size sentence and no `maxBytes`. `file_too_large` is a later, per-file check and does include `maxBytes`.
4. `[ApiController]` model state. A body that cannot bind is 400 `invalid_request_body`. A body-limit exception stored in model state is 413 `request_too_large` (`ExceptionHandlingMiddleware.InvalidModelStateResponse`).
5. `[RequireWorkspaceRole]`: 400 `invalid_route_id`, 404 `not_found` (missing id or non-member, same body), 403 `workspace_access_denied`.
6. `ValidateAndThrowAsync`: 400 `validation_failed`, plus `errors` and `fieldCodes`.
7. The service throws an `AppException` whose `code` and `detail` match a catalog row (`assignee_not_member`, `project_key_taken`, `forbidden`, …).
8. A Postgres unique violation or parent-link failure that escaped the service is mapped in `ExceptionHandlingMiddleware` through `UniqueConstraintConflicts` and `ParentLinkConflicts` to the catalog 409. It must not surface as 500.
9. Anything else is 500 `internal_error` with `traceId`. The detail is `An unexpected error occurred.` The middleware replaces the exception text unless the exception is an `AppException` or a `ValidationException`.

`not_found` and `unauthorized` are still generic codes. The catalog defers `<entity>_not_found` and `invalid_credentials` / `refresh_token_invalid` to a later card (WL-64). Until then, new missing-resource throws use `new NotFoundException("Resource was not found.")`. The `(entityName, key)` constructor remains in `ProjectService` and `BoardService` and emits a different sentence; leave those call sites, and do not copy them.

Login and refresh throw `AuthenticationFailedException`, code `unauthorized`, with details `Invalid credentials.` and `Refresh token is invalid or expired.` The catalog's single `unauthorized` row says `A valid access token is required.` Those three sentences share one code today.

A specific conflict uses the catalog detail, not the one-argument `ConflictException`, which sets code `conflict` and keeps whatever message it was given (`BoardService`: `Project has no columns. Create a column first.`). Password failures during register still return `conflict` with Identity's text until `registration_failed` is emitted; the catalog already says so.

## Tests

One test project: `tests/Worklane.IntegrationTests`. xUnit, FluentAssertions, `WebApplicationFactory`, Testcontainers.

Put a test in the feature folder: `Auth/`, `Board/`, `Projects/`, `Workspaces/`. HTTP pipeline behaviour stays in `Web/`. Host and startup checks stay in `Infrastructure/`. A pure function can sit in `Domain/` (`WorkingDaysTests` does not boot the host).

Name the file for the behaviour (`DefaultColumnLocaleTests`, `TaskCommentTests`). `Board/ReviewFollowUpTests.cs` is a round-based name inside a feature folder. `E2EBackendFixTests` and `E2E1RegressionTests` keep those class names and live in the feature folder for the behaviour each case covers. Add the next case to the feature file, or add a behaviour-named file beside it.

A workspace-scoped change needs a non-member (404 `not_found`, same body as a missing id) and a role that is too low (403 `workspace_access_denied`). `RequireWorkspaceRoleFilterTests` is the existing shape.

## Review checklist

- Branch is from `develop`. The pull request targets `develop` and is left unmerged.
- New endpoint: `[Authorize]` or `[AllowAnonymous]`, and `[RequireWorkspaceRole]` when the route names a workspace-owned id. Workspace id is a route value.
- New table: entity, `IEntityTypeConfiguration`, and exactly one `HasQueryFilter` in `WorklaneDbContext`. Archived rows are excluded there when the product hides them.
- `IgnoreQueryFilters` has an explicit workspace predicate, and a comment would not be the only thing that says why.
- Validator is `<Request>Validator`, constructor-injected, `ValidateAndThrowAsync` before the service call.
- Every new `code` is a row in `docs/api-error-codes.md`. Detail and extension names match that row. Status comes from the exception subclass.
- Unique indexes that can fire on a double submit are mapped in `UniqueConstraintConflicts`, or handled before `SaveChanges` with the same code.
- Tests live under the feature folder and cover the non-member 404. `ReadFromJsonAsync` uses `TestAuthHelper.JsonOptions`.
- `dotnet test Worklane.slnx --configuration Release` is green. No migration unless the ticket is a schema change, and then only one migration in the pull request.

## Do not use

- MediatR, `IRequest`, command handlers, or query handlers. The application layer is request records and `I*Service`.
- A workspace id, project id, or role taken from the JSON body for authorization.
- A second `HasQueryFilter` in an `IEntityTypeConfiguration`. EF Core keeps the last call.
- `IgnoreQueryFilters()` with no explicit predicate.
- A hand-written membership check inside a controller action. The hub is the exception, because it has no MVC route.
- `[FromServices] IValidator<T>` on a new action.
- A validation filter that runs before `[RequireWorkspaceRole]`.
- A new `code`, or a new `detail` for an existing `code`, that is not in `docs/api-error-codes.md`.
- `NotFoundException(entityName, key)` in new code.
- `throw new ConflictException("some sentence")` when the catalog has a specific code.
- A test file under `Fixes/` or `Regression/`, or a class named for the review round.
- `FirstAsync()` (or any further LINQ operator) on `SqlQuery` of `INSERT … RETURNING`.
- `HasDefaultValue` on an enum whose `0` member is a real value, without `HasSentinel` set outside the enum.
- Integer positions for card order.
- A reference from `Worklane.Domain` to EF Core or ASP.NET.
- Returning `exception.Message` from a new middleware path. `ExceptionHandlingMiddleware` already chooses the client sentence.
