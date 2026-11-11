# WL-RESTORE-DESIGN — Card restore (unarchive)

- **Status:** design only. WL-RESTORE is on hold. This file does not start implementation.
- **Base:** `develop` @ `e99d3fb`.
- **Role plan:** [PR #43](https://github.com/arutdevs/Worklane-Api/pull/43) (`2c53b973`), `docs/specs/WL-ROLES-roles-permissions.md`. Not merged. The role check below is a hook until that plan is approved.
- **Parent rules:** `docs/specs/WL-PARENT-parent-subtasks.md` on this branch (A.5–A.7). There is no section P-9 in that file. The no-auto-restore rule is A.7, line 130.
- **Conventions:** `AGENTS.md`, `docs/conventions-api.md`. New work copies the Projects slice. It does not add MediatR.

Code citations are `develop` @ `e99d3fb`. PR #43 cites `TasksController.cs:139` for archive. After `91cb6d7` that action is `TasksController.cs:148-154`.

## 0. Names

`docs/conventions-api.md:49` and `:236` forbid command types, query types, and handlers. `RestoreTaskCommand`, a restore handler, and `ListArchivedTasksQuery` are **not** new types.

| Ticket name | What to add |
|---|---|
| `ListArchivedTasksQuery` | **New** `IBoardService.ListArchivedTasksAsync` |
| `RestoreTaskCommand` | **New** `IBoardService.RestoreTaskAsync` |

Both methods go on the existing `IBoardService` (`src/Worklane.Application/Board/Interfaces/IBoardService.cs`). `BoardService` is already a partial class. List sits next to `GetTasksAsync` (`BoardService.cs:421`). Restore sits next to `ArchiveTaskAsync` (`BoardService.Parents.cs:151`).

## 1. Endpoints

Both actions are **new** methods on the existing `TasksController` (`src/Worklane.WebApi/Controllers/TasksController.cs`). The class already has `[ApiController]` and `[Authorize]` (`:12-14`). Do not add a controller.

| | R1 | R2 |
|---|---|---|
| Route | `GET /api/projects/{projectId}/tasks/archived` | `POST /api/tasks/{taskId}/restore` |
| Place next to | `GetTasks` (`:40-51`) | `ArchiveTask` (`:144-154`) |
| Route constraint | `{projectId:guid}` | `{taskId:guid}` |
| Body | none | none |
| Success | 200 `ArchivedTaskListDto` | 200 `TaskItemDto` |

`TaskItemDto` already exists (`src/Worklane.Application/Board/Dtos/BoardDtos.cs:20-83`), including `ArchivedAt` (`:82`). R2 returns that record with `isArchived: false` and `archivedAt: null`.

**New** records in `BoardDtos.cs`:

- `ListArchivedTasksRequest(int Page = 1, int PageSize = 25)` — query string. Omitted keys use those defaults (AC 1: page size 25).
- `ArchivedTaskListDto(IReadOnlyList<ArchivedTaskDto> Items, int Total)` — JSON `items`, `total`. `total` is the count before paging.
- `ArchivedTaskDto(Id, DisplayKey, Title, ColumnId, ColumnName, ArchivedAt, ArchivedBy, ParentTaskId, ChildCount)`.
- `ArchivedByDto(Guid Id, string DisplayName)`.

`archivedBy` is always `null`. `TaskItem` has `ArchivedAt` (`TaskItem.cs:44`) and no `ArchivedById`. The archived activity row stores `ActorUserId` (`TaskActivity.cs:22`), and this design does not read it. The Web app shows "—".

`displayKey` is `$"{projectKey}-{number}"`, the same concatenation as `ToDto` (`BoardService.cs:705`).

`childCount` is active children only, the same meaning as `TaskItemDto.ChildCount` (`BoardDtos.cs:75-76`). One grouped count, the shape of `LoadParentLinksAsync` (`BoardService.Parents.cs:379-388`), on the normal query filter. Archived children are not counted. A parent archived with its children therefore shows `0`.

`columnName` is the name of the card's **current** `ColumnId`, not a name saved at archive time. Nothing stores the column from the moment of archive.

**New** `ListArchivedTasksRequestValidator` in `src/Worklane.Application/Board/Validators/`, one class per file (`conventions-api.md:61`). Rules: `Page >= 1`, `PageSize` in `1..100`. `100` is a **new** cap; no paging constant exists today. `AddValidatorsFromAssembly` already picks validators up (`conventions-api.md:171`).

`TasksController` already takes `IValidator<T>` in the constructor (`:18-37`) and calls `ValidateAndThrowAsync` as the first line of write actions (`CreateTask` at `:61`). R1 adds one constructor parameter, `IValidator<ListArchivedTasksRequest>`, and calls `ValidateAndThrowAsync` as the first line of the action, after the role filter has run (`conventions-api.md:173-175`). Do not use `[FromServices]` (`:241`).

R2 has no body and no query, same as `ArchiveTask` (`:150-153`). It gets no validator. An empty validator would be a new type with nothing to check.

Sort: `ArchivedAt` descending, then `Id` ascending, so a page is stable when two cards share a timestamp. Offset `(page - 1) * pageSize`.

R1 404s when the project is missing or itself archived, with `new NotFoundException("Resource was not found.")`. `GetTasksAsync` uses the `(entity, key)` constructor (`BoardService.cs:424-425`). New code must not copy that (`conventions-api.md:206` and `:244`). The project load stays on the global filter (`Projects` filter hides archived projects, `WorklaneDbContext.cs:53-56`). The task load cannot.

## 2. List query and restore method

The task filter hides archived rows (`WorklaneDbContext.cs:69-72`). Comments, attachments, and activity follow the task (`:74-87`), so they reappear when `IsArchived` becomes false. No row is copied.

R1 task query (review blocker if the workspace predicate is missing, `WL-PARENT` A.8 line 138 and `conventions-api.md:184` and `:239`):

```csharp
var workspaceId = RequireWorkspaceId();
var tasks = _db.TaskItems
    .IgnoreQueryFilters()
    .Where(t => t.ProjectId == projectId
        && t.IsArchived
        && t.Project.WorkspaceId == workspaceId);
```

`RequireWorkspaceId()` is `BoardService.Parents.cs:425-426`. `GetWorkspaceIdForTaskAsync` (`BoardService.cs:484-491`) also calls `IgnoreQueryFilters()` and has **no** workspace predicate. That helper runs before a tenant is pinned. Do not copy it for the list or the restore load.

Restore reuses `LoadLockedAsync` (`BoardService.Parents.cs:511-520`), which already has both:

```csharp
return await _db.TaskItems
    .IgnoreQueryFilters()
    .Include(t => t.Project)
    .Where(t => ids.Contains(t.Id) && t.Project.WorkspaceId == workspaceId)
    .ToListAsync(ct);
```

Restore runs inside `InLinkTransactionAsync` (`:267-288`): one Read Committed transaction, `SaveChanges` inside, `CommitAsync` before the method returns. Broadcast happens after that return, which is what `ArchiveTaskAsync` does (`:154` and `:192-218`).

Steps inside the transaction, in this order:

1. `LockChildAndParentAsync` (`:447-471`) when the card might have a parent. The SQL locks the card and its current parent in id order with `FOR UPDATE OF t`, and it does not filter `IsArchived`. A card with no parent locks that one id through `LockIdsAsync` (`:428-444`). Lock task rows first, then the destination column row (step 6), and use that order on every restore so two restores cannot deadlock each other.
2. `LoadLockedAsync`. No row → `new NotFoundException("Resource was not found.")`. Do not use `new NotFoundException(nameof(TaskItem), taskId)`. Archive still does (`:160-161`). Leave that call site; do not copy it.
3. `IsArchived == false` → map the current `TaskItemDto` and return. No `SaveChanges`, no activity, no event. `UpdatedAt` stays as it was.
4. Parent row present and `parent.IsArchived` → 409 `parent_archived`. Do not call `RejectOrUnchangedAsync`. That method throws `card_archived` when either card is archived (`:300-301`), which is the link rule, not the restore rule.
5. Parent row present and active → count active children with the filtered query at `:337` (`ParentTaskId == parent.Id`; the filter drops archived rows). The parent row is already locked, so a concurrent link (it locks the same parent) cannot both pass. `active >= _maxChildren` → the same throw as `:340-343`, code `parent_child_limit`, extension `maxChildren`. `_maxChildren` is `Tasks:MaxChildrenPerParent` (`TaskSettings.cs:9`, `appsettings.json:63-65`, assigned at `BoardService.cs:41`). Default 50. Archived children do not count.
6. Destination column is the card's current `ColumnId` (see §5). Lock that `BoardColumns` row. **New** SQL, same shape as `LockIdsAsync`:

```sql
SELECT c."Id"
FROM "BoardColumns" AS c
INNER JOIN "Projects" AS p ON p."Id" = c."ProjectId"
WHERE c."Id" = @columnId
  AND p."WorkspaceId" = @workspaceId
FOR UPDATE OF c
```

7. Set order with the existing `NextOrderAsync` (`:538-544`): `max(Order)` among rows the filter still sees (active, this tenant) plus `65536`. Card order is a `double` (`TaskItem.cs:24`, `AGENTS.md` "Card order"). `conventions-api.md:249` forbids integer positions. The PO line "position = max + 1" means the end of the visible column, not a new integer column. `IX_TaskItems_ColumnId_Order` is not unique (`docs/api-error-codes.md:86`), so two restores that skip the column lock can store the same `Order` and still return 200. The lock is what makes the end-of-column rule hold.
8. Set `IsArchived = false`, `ArchivedAt = null`, `UpdatedAt = now`. Leave `CreatedAt`, `Number`, `ParentTaskId`, `StartedAt`, `CompletedAt`, and `ColumnId` (unless §5 reassigns the column). This is the inverse of `ArchiveCard` (`:581-585`), which sets `IsArchived = true` and `ArchivedAt = now` and does not clear `ParentTaskId`.
9. Append the activity row (§6). `SaveChanges`. Return the broadcast plan. After commit: `TaskRestored`, and `TaskUpdated` for the parent when `ParentTaskId` is set. Wrap each send in the same try/catch as `:197-218`, so a notifier failure stays a warning and the HTTP result stays 200. `CreateLinkedTaskAsync` (`:258-259`) does not catch; restore follows archive, not create.

Children of a restored parent are not loaded and not updated (A.7 line 130, AC 7).

## 3. Errors

Pipeline order is `conventions-api.md:194-204`, which matches the WL-PARENT A.5 order (`WL-PARENT-parent-subtasks.md:91`): 401, then 429, then 413, then routing 404, then binding 400, then non-member 404, then role 403, then validator 400, then rule 409s.

| Step | R1 / R2 |
|---|---|
| 401 | Missing or invalid JWT: empty body (`Program.cs:249`). Filter with an empty user id: 401 `unauthorized`, detail `A valid access token is required.` (`RequireWorkspaceRoleAttribute.cs:41-44`). |
| 429 | No `[EnableRateLimiting]` on these actions. Archive has none. Nothing on R1 or R2 returns 429. |
| 413 | `ContentLengthLimitMiddleware` (`Program.cs:252`, middleware `:31-45`): declared `Content-Length` over the limit is 413 `request_too_large`. A normal empty restore body does not hit it. |
| 404 routing | `{id:guid}` fails to match. Framework 404, before the action. This body is not the problem document below. |
| 400 binding | R1 only. `page` or `pageSize` not an integer → 400 `invalid_request_body` (`conventions-api.md:199`). R2 has nothing to bind. |
| 404 non-member | Filter `:86-100` and `:114-115`. Missing id, purged row, other workspace, or no membership. One body. |
| 403 role | Filter `:103-106`, and only after the 404 branch. See §8. A Viewer never reaches a 409. |
| 400 validator | R1 only, first line of the action. `page < 1` or `pageSize` outside `1..100` → 400 `validation_failed` with `errors` and `fieldCodes`. |
| 409 | Service, after the lock: `parent_archived`, then `parent_child_limit`. |
| 200 | Already active. Not an error. Checked before the 409s. |

`GetWorkspaceIdForTaskAsync` ignores the archive filter, so an archived card still resolves a workspace (`BoardService.cs:484-491`). A Viewer is 403, not 404, for a card that is still in the table. A purged card is a missing row: the lookup returns null and every caller, including a Member, gets the filter 404. There is no purge method on `IBoardService` (the interface ends at `:28`). WL-58 is not on this branch.

404 from the filter and 404 from the service use the same sentence. Compare raw bodies the way `ParentTaskTests.cs:157` does. The service throw is `new NotFoundException("Resource was not found.")` (`NotFoundException.cs:9`). The `(entity, key)` constructor (`:11-12`) produces `Entity "TaskItem" ({id}) was not found.` and would fail the byte-identical check.

Problem documents are `application/problem+json`. `ExceptionHandlingMiddleware` (`:83-119`) sets `status`, `title`, `detail`, `instance`, and extension `code`. `traceId` is added only for 500 (`:92-93`). Extension names on an `AppException` are copied unless they are reserved (`:95-101`, reserved set `:178-180`). The filter builds the same fields itself (`RequireWorkspaceRoleAttribute.cs:117-127`) and does not go through the middleware.

404 `not_found` (filter or catalog throw):

```json
{
  "status": 404,
  "title": "Not Found",
  "detail": "Resource was not found.",
  "instance": "/api/tasks/{taskId}/restore",
  "code": "not_found"
}
```

403 when the hook is the PR #43 proposal (`:103-106`):

```json
{
  "status": 403,
  "title": "Forbidden",
  "detail": "You do not have sufficient permissions in this workspace.",
  "instance": "/api/tasks/{taskId}/restore",
  "code": "workspace_access_denied"
}
```

The PO sketch says 403 `forbidden`. PR #43 replaces that sketch so restore matches archive. `forbidden` is a different emitted code (`docs/api-error-codes.md:30`), used by comment and attachment checks, not by `[RequireWorkspaceRole]`. This design does not throw `forbidden` for restore.

409 `parent_archived`. The catalog row already exists and is `planned (WL-PARENT)` (`docs/api-error-codes.md:70`), detail `Restore the parent card first.` **New** constants on `ParentTaskErrors`. That file has no `parent_archived` member. Its last constant is `ManualSource` at `:45`.

```csharp
public const string ParentArchived = "parent_archived";
public const string ParentArchivedDetail = "Restore the parent card first.";
```

```csharp
throw new ConflictException(ParentTaskErrors.ParentArchived, ParentTaskErrors.ParentArchivedDetail);
```

Thai `กู้คืนการ์ดหลักก่อน` is a Web string (`WL-PARENT` line 237). The API detail stays English (`conventions-api.md:165`). The implementation PR flips the catalog status to `emitted`. This design PR does not edit the catalog.

```json
{
  "status": 409,
  "title": "Conflict",
  "detail": "Restore the parent card first.",
  "instance": "/api/tasks/{taskId}/restore",
  "code": "parent_archived"
}
```

409 `parent_child_limit` reuses the throw at `BoardService.Parents.cs:340-343`. Detail template `ParentTaskErrors.ParentChildLimitDetail` (`ParentTaskErrors.cs:22`). Extension key `maxChildren` (`:42`). No new code, no new detail.

```json
{
  "status": 409,
  "title": "Conflict",
  "detail": "This card already has the maximum of 50 subtasks.",
  "instance": "/api/tasks/{taskId}/restore",
  "code": "parent_child_limit",
  "maxChildren": 50
}
```

`column_missing` is not a catalog code and is not added here. See §5.

A failed restore (404 or 409) writes no activity and sends no event. The row stays archived.

## 4. Events

**New** `TaskRestoredEvent` in `BoardDtos.cs`, beside `TaskArchivedEvent` (`:120-124`):

```csharp
public record TaskRestoredEvent(Guid ProjectId, Guid TaskId, Guid ColumnId, Guid? ParentTaskId);
```

**New** `IBoardNotifier.TaskRestoredAsync(Guid projectId, TaskRestoredEvent payload, CancellationToken ct = default)` (`IBoardNotifier.cs:10-23` has no restore method). **New** `SignalRBoardNotifier` method sends `"TaskRestored"` to `NotificationsHub.GroupName(projectId)`, which is `project:{projectId}` (`NotificationsHub.cs:93`, used at `SignalRBoardNotifier.cs:44`). `TaskArchivedAsync` (`SignalRBoardNotifier.cs:26-27`) is the pattern.

The payload does **not** carry `TaskItemDto`.

`TaskArchived` is the slim inverse (`BoardDtos.cs:124`). `TaskCreated` and `TaskUpdated` carry `TaskItemDto` because those clients paint a card or a new count from the payload (`IBoardNotifier.cs:12-14`). Restore only has to tell the board which column gained a card. The PO text says clients recompute `taskCount` and WIP from `columnId`. The parent count is a separate `TaskUpdated` of the parent, and that event already carries the full DTO, including `childCount` and `doneChildCount`. A second full DTO on `TaskRestored` would duplicate that and force every notifier fake to take a large payload for a signal the archived event already proved can be small. The client that has the board open calls the existing `GET /api/tasks/{taskId}` (`TasksController.cs:66-72`) after the event. That GET works once the card is active, because the filter no longer hides it. The ~2 s budget in AC 3 includes that GET.

`TaskUpdated` for the parent uses the existing `NotifyParentCountsAsync` (`BoardService.Parents.cs:360-370`): reload the parent through the filter, `MapTaskAsync`, `TaskUpdatedAsync`. Call it only when the restore committed and `ParentTaskId` was set. The parent is active in that case (step 4 rejected an archived parent). Do not send it for a parent-less card, a 409, a 404, or the idempotent 200.

Both sends run only after `CommitAsync`. `SaveChanges` inside an open transaction is not enough: a later rollback would have published a lie.

`DeleteBroadcastFails` (`tests/Worklane.IntegrationTests/Board/ReviewFollowUpTests.cs:236-251`) and `ArchiveBroadcastFails` (`:318-331`) implement the whole interface. Each needs a **new** `TaskRestoredAsync` or the solution does not build. `Program.cs:181` registers `SignalRBoardNotifier` and needs no new registration.

## 5. Column and position

`BoardColumn` has no deleted flag (`src/Worklane.Domain/Entities/BoardColumn.cs`). Delete is a hard remove: `_db.BoardColumns.Remove` at `BoardService.cs:134` and `:201`.

`TaskItem.ColumnId` is a required FK with `DeleteBehavior.Restrict` (`TaskItemConfiguration.cs:39-42`). A column row cannot disappear while a card still points at it. `SaveColumnDeleteAsync` (`BoardService.cs:553-563`) turns a leftover FK violation into 409 `column_delete_conflict`.

`DeleteColumnAsync` (`:117-203`) loads archived cards on purpose (`IgnoreQueryFilters` plus `t.Project.WorkspaceId == workspaceId`, `:126-130`). If any card remains, the delete moves every card, archived included, onto `targetColumnId` and rewrites `Order` (`:195-198`), then removes the column. The last column cannot be deleted while archived cards exist (`last_column_has_archived_cards`, `:147-159`).

So a successful delete does not leave a dangling `ColumnId`. The card's "original column" is not stored. After a delete, `ColumnId` is already the column the cards were moved to. Restore places the card at the end of that column. No 409, no toast.

**Q-3, pending the owner.** Recommended behaviour if a card's `ColumnId` does not resolve (no current API path does this): put the card on the first column of the project by `BoardColumn.Order`, which is the create fallback at `BoardService.cs:220-224`, return 200 `TaskItemDto` with that `columnId`, and let the Web app toast when the returned `columnId` differs from the column on the archived row. Do not add a `columnReassigned` field to `TaskItemDto`. That record is every board payload (`:20`). Do not add `column_missing` while this recommendation stands. If the project has no column either, that state is also unreachable (`last_column_has_archived_cards`). A new catalog code would be an implementation change only after the owner rejects the fallback.

Position is always `NextOrderAsync` under the column row lock in §2, including the Q-3 fallback column.

## 6. Activity and audit

**New** enum member, appended after `Archived`. Do not insert it earlier. Current enum (`TaskActivityType.cs:8-23`) ends at `Archived` (`:22`).

`TaskActivity.Type` is stored as a string (`TaskActivityConfiguration.cs:15-18`, `character varying(32)` from migration `20261017180618_AddCommentsAttachmentsActivityAndWip`). `Restored` fits in 32 characters. There is no check constraint on the stored name. Appending the member does not need a migration and does not need `HasSentinel` (that trap is for an integer column with a default; this column has no default, `TaskActivityType.cs:5-7`).

One **new** row on the restored card, same shape as `ArchiveCard` (`BoardService.Parents.cs:586-594`): new `Guid`, `Type = TaskActivityType.Restored`, `ActorUserId` = the caller, `CreatedAt = now`, `Details = "{}"`. Append only. The idempotent path writes nothing.

The parent does **not** get a "child restored" row. Archive does not write one: `ArchiveCard` only adds `TaskActivityType.Archived` on the card being archived. WL-PARENT's parent rows are `ChildAdded` and `ChildRemoved`, and they are written by link and unlink (`AddLinkActivities`, `:611-617`), not by archive. The parent's `TaskUpdated` is the archive mirror for counts (`:209-214`). A parent activity row would be a new enum value with no archive counterpart. That is Q-6.

There is no card-audit writer. `WorkspaceMembershipEvent` (`WorklaneDbContext.cs:32` and `:91-93`) records membership changes only. `sprint-proposal.md` is not in this checkout, so this design does not add a second audit row from a line it cannot read. The `Restored` activity row is the history. A later card-audit list can subscribe beside archive; it is not a table in this ticket.

## 7. Database

No migration for the behaviour in §1–§6. `IsArchived` and `ArchivedAt` already exist (`TaskItem.cs:41-44`, migration `20261107171703_AddTaskArchivedAt`). This design PR adds no migration file.

Optional index, for the implementation PR, one migration if they take it. The list is `ProjectId` + `IsArchived` + `ArchivedAt DESC`. Existing task indexes are `(ProjectId, Number)` unique, `(ColumnId, Order)`, and partial `(ProjectId, ParentTaskId)` (`TaskItemConfiguration.cs:61-73`). None serves that list.

Follow `AddTaskParent.cs:25-29` (Up) and `:56-58` (Down). Do not use `CREATE INDEX CONCURRENTLY`; `MigrateAsync` runs migrations in a transaction (`Program.cs:237`).

Up:

```csharp
migrationBuilder.CreateIndex(
    name: "IX_TaskItems_ProjectId_ArchivedAt",
    table: "TaskItems",
    columns: new[] { "ProjectId", "ArchivedAt" },
    descending: new[] { false, true },
    filter: "\"IsArchived\" = TRUE");
```

Down: `migrationBuilder.DropIndex("IX_TaskItems_ProjectId_ArchivedAt", "TaskItems")`.

No backfill, no column change. Down drops only that index. Shipping without the index is still correct.

## 8. Role hook

The only check is `[RequireWorkspaceRole]` on the action. No membership test inside the action (`conventions-api.md:240`). The filter already returns 404 for a non-member before it returns 403 (`RequireWorkspaceRoleAttribute.cs:97-106`).

| Action | Attribute, if PR #43 is approved |
|---|---|
| R1 | `[RequireWorkspaceRole(WorkspaceRole.Viewer, WorkspaceResourceType.Project, "projectId")]` |
| R2 | `[RequireWorkspaceRole(WorkspaceRole.Member, WorkspaceResourceType.Task, "taskId")]` |

`WorkspaceRole` is `Viewer = 0`, `Member = 1`, `Admin = 2`, `Owner = 3` (`WorkspaceRole.cs:3-8`). A higher value passes a lower minimum (`RequireWorkspaceRoleAttribute.cs:103`). Admin and Owner pass a Member check.

**If PR #43 is approved as written.** R1 is Viewer+. There is no role below Viewer, so R1 does not return 403. A non-member on R1 or R2 gets 404 `not_found`, the same body as a random id. R2 is Member+. A Viewer gets 403 `workspace_access_denied` and never a 409. The Web app shows Restore only for Member+ and still shows the list to a Viewer (Q-4). AC 5's 403 test asserts `workspace_access_denied`, not `forbidden`.

**If PR #43 is not approved.** Do not guess a matrix and do not ship AC 5's 403 as final. The attribute stays the hook: the only edit is the minimum role on those two lines. The PO sketch of `forbidden` is not implemented in advance. A hand-written role check in `RestoreTaskAsync` is out of pattern even if the owner later asks for `forbidden`. Implementation of the 403 path waits on the owner. The rest of this design (list query, locks, `parent_archived`, cap, events, activity) does not depend on which role the attribute names.

## 9. Tests

**New** `tests/Worklane.IntegrationTests/Board/TaskRestoreTests.cs`. xUnit. `ReadFromJsonAsync` uses `TestAuthHelper.JsonOptions` (`AGENTS.md` "String enums in tests"). Timestamps use `BeCloseTo(..., TimeSpan.FromMilliseconds(1))`, as `TaskCommentTests.cs:54` and `AGENTS.md` "Timestamps". Do not copy the five-second window in `ArchiveTimestampTests.cs:59` and `:97`.

No database was used to write this design.

| # | AC | Assert |
|---|---|---|
| 1 | 2 | Archived card with a comment and a file. Member restore → 200, `archivedAt` null, `isArchived` false. `Order` equals the previous active max in that column plus `65536` (end of column). GET task, comments, attachments, and activity all 200 again. |
| 2 | 3 | Second connection in `project:{projectId}` receives `TaskRestored` with `projectId`, `taskId`, `columnId`, `parentTaskId`. A connection in another project receives nothing. Parent present → that connection also receives `TaskUpdated` for the parent, and `childCount` / `doneChildCount` include the restored child. |
| 3 | 4 | One `TaskActivityType.Restored` row, `ActorUserId` is the caller, `Details` is `{}`. Second call (test 7) does not add another. |
| 4 | 5 | User in another workspace, R1 and R2: 404 body byte-identical to a random id (`ParentTaskTests.cs:157`). No row changes. |
| 5 | 5 | Viewer calls R2: 403 `workspace_access_denied`. Written when §8's approved branch is the one being built. Not written against a guessed role if PR #43 is still open. Viewer calls R1: 200 when the approved minimum is Viewer. |
| 6 | 6 | Child and parent both archived. Restore child → 409 `parent_archived`, detail `Restore the parent card first.`, no `maxChildren`. Child stays archived. No `TaskRestored`, no new activity. |
| 7 | 7 | Restore parent → 200, parent active, child still archived (read with `IgnoreQueryFilters` in the test). Then restore child → 200, and `TaskUpdated` for the parent carries the new counts. |
| 8 | 8 | Parent already has 50 active children (`TaskSettings.MaxChildrenPerParent`). Restore of an archived child → 409 `parent_child_limit` and `maxChildren` 50. Child stays archived. |
| 9 | 8 | Race, same shape as `ParentTaskTests.Cap_SerializesTwoConcurrentLinks` (`:323`): parent has 49 active children; one restore of an archived child and one new link. Exactly one 200. The other is 409 `parent_child_limit` or `parent_link_conflict`. Active children never 51. |
| 10 | 9 | Restore of an active card → 200 current DTO. `UpdatedAt` unchanged (`BeCloseTo` 1 ms against the previous value). No activity row, no hub event. |
| 11 | 10 | Delete the `TaskItems` row (there is no purge route). Restore → 404 body byte-identical to a random id. |
| 12 | Q-3 | Reachable case: delete the column with a `targetColumnId` so archived cards move (`BoardService.cs:195-198`). Restore → 200, `columnId` is the target, order is the end of that column. The dangling-column case cannot be seeded while the FK is `Restrict`. Do not drop the FK for a test. Add the fallback assertion only after the owner accepts Q-3 and a reachable setup exists. |
| 13 | 2, 4 | `UpdatedAt` and the `Restored` row's `CreatedAt` are `BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(1))`. |

**New** Vitest specs in Worklane-Web. This checkout has no `web/` tree (`AGENTS.md` "Layout"), so paths there are new and are not named here.

1. Archived list renders key, title, column name, archived date, an em dash for archived-by, and a parent chip when `parentTaskId` is set. Empty page and the pager are covered.
2. Viewer: no Restore button. Member: Restore shown. Child whose parent is archived: button disabled, tooltip `task.restoreParentFirst` (`กู้คืนการ์ดหลักก่อน` / `Restore the parent card first.`), link to the parent row.
3. Restore success: row leaves the list, toast `task.restored` with a "View on board" link.
4. Error map: `parent_archived` → `task.restoreParentFirst`; `parent_child_limit` → `task.restoreLimit` with `maxChildren`; 404 removes the row; other failures → `task.restoreError`. No hard-coded English in the Thai UI (AC 11). Keys from the PO spec: `task.archivedList.title`, `task.restore`, `task.restored`, `task.restoreError`, `task.restoreParentFirst`, `task.restoreLimit`, `task.restoredByOther`.
5. `TaskRestored` inserts the card in `columnId` and recomputes that column's count and WIP. When `parentTaskId` is set, the parent's progress updates from the following `TaskUpdated`.

## 10. Open questions and size

| Id | Status | This design |
|---|---|---|
| Q-1 Who may restore? | **Blocking.** PR #43 proposes Member+ and 403 `workspace_access_denied`. Not approved. | Hook in §8. No other check. |
| Q-2 Auto-restore children? | Recommended closed. WL-PARENT A.7 line 130: parent only. PR #43 says the same. There is no P-9 section to follow. Owner can still override. | Children stay archived. |
| Q-3 Deleted column? | **Pending the owner.** | §5. Recommend first column by `Order`, plus a Web toast when `columnId` changes. Unreachable through current delete. `column_missing` is not added. |
| Q-4 Viewer and the list? | **Pending.** PR #43 proposes Viewer+ may list, Member+ may restore. | R1 attribute is Viewer only if that proposal is approved. |
| Q-5 Purge-date hint? | Out of this ticket. No auto-purge job in this codebase. | Not designed. |
| Q-6 Parent "child restored" activity? | **New.** | Recommend no row. Archive does not write one (§6). Say if the parent history should gain a new enum value anyway. |

Size, same bucket as the PO note. The work is two actions on `TasksController`, two methods on `IBoardService`, one enum member, one notifier method, two test-fake methods, the R1 validator, and `TaskRestoreTests`. No migration unless the optional index is taken. No MediatR type. Backend about 1.5 man-days, Web about 1.5, QA about 0.5. The ticket stays on hold until the owner starts it and answers Q-1.

## Implementation checklist (when the hold lifts)

Existing files to edit: `TasksController.cs`, `IBoardService.cs`, `BoardService.cs`, `BoardService.Parents.cs`, `BoardDtos.cs`, `ParentTaskErrors.cs`, `TaskActivityType.cs`, `IBoardNotifier.cs`, `SignalRBoardNotifier.cs`, `ReviewFollowUpTests.cs`, `docs/api-error-codes.md` (status of `parent_archived` only). `TaskItemConfiguration.cs` only if the index is included.

**New** files: `ListArchivedTasksRequestValidator.cs`, `TaskRestoreTests.cs`, and at most one migration for `IX_TaskItems_ProjectId_ArchivedAt`. No `RestoreTaskCommand`, no `ListArchivedTasksQuery`, no handler, no `ArchivedById` column, no purge route.
