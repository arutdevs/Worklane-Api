# WL-IMPORT — CSV/XLSX import and project export

- **Status:** Final contract for BE and FE. Open product questions in §C. Defaults in that table are the contract until the owner changes them.
- **Code refs:** `develop` @ `834ce8f`. Paths below were read in this tree. Web file names were not re-verified; FE checks them before starting.
- **Checked against:** `docs/conventions-api.md`, `docs/api-error-codes.md`, `AGENTS.md`. No `worklane-pattern-review` skill is in this repo (only `.claude/skills/worklane-board/SKILL.md`).
- **Order:** Part A is the API contract. Part B (Worklane-Web) mocks A and does not merge ahead of the API PR.

## 0. Scope

**In (v1):** one project at a time. Upload a `.csv` or `.xlsx`, pick a preset (`generic`, `jira`, `asana`, `trello`), adjust the column map, dry-run with per-row errors, commit in two passes inside one transaction (insert cards, then link parents), skip cards that already have the same external id for that project and source, undo the batch under the rules in A.6. Export the project's active cards as CSV or JSON.

**Out:** AI column mapping, MCP, Trello JSON (the free export; CSV is the preset), `.xls` / `.xlsm`, auto-creating columns, labels, comments, attachments, checklists, watchers, updating an existing card on re-import, flattening a third hierarchy level, cross-project or cross-workspace import, exporting archived cards, a public read API for export logs, restore-after-undo, a commit queue. See §C.

**Definitions**

- *Active card* = `TaskItem.IsArchived = false` (the query filter already hides archived rows).
- *Source* = the preset stored on the batch and on each created card: `generic` | `jira` | `asana` | `trello`.
- *External id* = the source system's id, a non-empty string, unique per `(ProjectId, Source)`.
- *Open batch* = status `uploaded`, `mapped`, or `previewed`, and `ExpiresAt` in the future.
- *Pristine* = the undo rule in A.6. Anything else was edited after import.
- *Blocking row* = a row that cannot be committed as specified. *Warning* = the row still commits, with a recorded code.

### 0.1 What exists today (do not invent past this)

| Fact | Where |
|---|---|
| Card entity is `TaskItem` (not `Task`). Fields: `Title` (max 500), `Description` (max 10_000), `AssigneeId` (`Guid?`), `Priority`, `DueDate`, `EstimateDays`, `ColumnId`, `Order` (`double`), `StartedAt`, `CompletedAt`, `ParentTaskId`, `Number`, `IsArchived`. No external id, no source, no labels. | `src/Worklane.Domain/Entities/TaskItem.cs` |
| Priority is `Low`, `Medium`, `High`, `Urgent`. DB default is Medium. `Low` is 0, so the column uses `HasSentinel((TaskPriority)(-1))`. An imported Low must be written explicitly. | `src/Worklane.Domain/Enums/TaskPriority.cs`, `TaskItemConfiguration.cs` |
| `StartedAt` / `CompletedAt` are stamps, not planned dates. Create sets `StartedAt` when the column `IsInProgress`, `CompletedAt` when `IsDone`. | `BoardService.Parents.cs` `NewTask` |
| Display key is computed `{Project.Key}-{Number}`, not stored. Numbers come from `NextTaskNumberAsync` (`INSERT … ON CONFLICT … RETURNING`, column alias `AS "Value"`, materialized with `ToListAsync()`). | `BoardService.Parents.cs` |
| New-card order is `max(Order) + 65536` per column. | `BoardService.Parents.cs` `NextOrderAsync` |
| Assignee must be a **workspace** member. `ProjectMember` is not the check. Unknown user and non-member are different lookups today (`assignee_not_member` is only the non-member case on create). | `BoardService.EnsureAssigneeIsMemberAsync` |
| Columns: `Name` max 100, `Order`, `IsDone`, `IsInProgress`, `WipLimit`. WIP does not block create. No unique name. | `BoardColumn.cs`, `BoardColumnConfiguration.cs` |
| Default columns are `To Do` / `In Progress` / `Done`, or Thai `ต้องทำ` / `กำลังทำ` / `เสร็จแล้ว`. Import matches the project's actual names, not these seeds. | `WorkspaceLocale.cs` |
| Parent: at most one, same project (therefore same workspace), one level, cap `Tasks:MaxChildrenPerParent` (default 50), codes in `ParentTaskErrors`. | `docs/specs/WL-PARENT-parent-subtasks.md`, `TaskItem.ParentTaskId` |
| Activity types stop at `ParentCleared`. There is no `Archived` type and `IBoardNotifier` has no `TaskArchived`. Created-activity JSON is `{ columnId, columnName, title }`. Link JSON is `{ otherTaskId, otherDisplayKey, source }` with `source` `"manual"` today. | `TaskActivityType.cs`, `IBoardNotifier.cs`, `BoardService.Parents.cs` |
| No `Label` or `Tag` type under `src/Worklane.Domain`. A search of that tree finds neither. | `src/Worklane.Domain` |
| No `IHostedService` and no queue. Commit stays on the request. Retention is the only new background loop (A.10). | `src/` (no hosted service type) |
| Uploads: global Kestrel body is 2 MiB (`FileStorageSettings.GlobalRequestBodyLimitBytes`). Attachment routes raise their own ceiling. Client `Content-Type` is ignored. `.csv` and `.xlsx` are already in the attachment allow-list; import does **not** use that list or the attachment quota. | `Program.cs`, `FileStorageSettings.cs`, `FileSignature.cs`, `IFileStorage` |
| Viewer can already read member emails: `GET /api/workspaces/{id}/members` returns `WorkspaceMemberDto.Email`. | `WorkspacesController.cs`, `WorkspaceDtos.cs` |
| Tenancy: `[RequireWorkspaceRole]` then EF filters in `WorklaneDbContext` only. Non-member and missing id are both 404 `not_found`. Low role is 403 `workspace_access_denied`. | `RequireWorkspaceRoleAttribute.cs`, `WorklaneDbContext.cs` |
| A project with no columns throws `ConflictException` whose code is `conflict` and whose detail is `Project has no columns. Create a column first.` | `BoardService.CreateTaskAsync` |
| `Project → TaskItems` is Cascade. Parent self-FK is Restrict. No project hard-delete path is in this tree. | `TaskItemConfiguration.cs` |

Parent rules this spec reuses, unchanged: two levels only; parent and child in the same project; not self; cap `Tasks:MaxChildrenPerParent` (default 50) active children; archived cards are not linked. Codes emitted on the import path: `parent_self` (400 on the normal parent API; a **row** code here), `parent_depth_exceeded`, `parent_child_limit` (`maxChildren`). `child_has_children` is not emitted, because a re-import does not change the parent of a card that already exists.

---

# Part A — Backend (Worklane-Api)

Copy the Projects slice (`docs/conventions-api.md`): area `Imports`, request records, `IImportService` + `IProjectExportService`, constructor-injected validators, `ValidateAndThrowAsync` inside the action, `[Authorize]` + `[RequireWorkspaceRole]`. No MediatR. Workspace id comes from the route project, never from the body.

Routes carry `projectId`, so the filter is `WorkspaceResourceType.Project` and route param `projectId`. Do not add a resource type for the batch. A batch id that is not in that project is 404 `not_found` with detail `Resource was not found.` (`new NotFoundException("Resource was not found.")`).

## A.1 Data model

### `TaskItem` columns (nullable, no backfill)

| Column | Type | Null | Notes |
|---|---|---|---|
| `ImportSource` | `character varying(16)` | yes | `generic` \| `jira` \| `asana` \| `trello`. Null on cards not created by an import. |
| `ExternalId` | `character varying(200)` | yes | Trimmed. Case-sensitive. |
| `ImportBatchId` | `uuid` | yes | FK below. |

Check `CK_TaskItems_ExternalIdRequiresSource`: `"ExternalId" IS NULL OR "ImportSource" IS NOT NULL`.

Unique filtered index `IX_TaskItems_ProjectId_ImportSource_ExternalId` on `("ProjectId", "ImportSource", "ExternalId")` `WHERE "ExternalId" IS NOT NULL`. Archived rows stay in the index (the task filter hides them; the index must not).

Lookup index `IX_TaskItems_ImportBatchId` on `("ImportBatchId")` `WHERE "ImportBatchId" IS NOT NULL`.

FK `FK_TaskItems_ImportBatches_ImportBatchId`: `ImportBatchId` → `ImportBatches(Id)`, **`ON DELETE RESTRICT`**, not required.

Store both strings with `HasConversion<string>()` if a C# enum is used. No `HasDefaultValue` on these columns.

### `ImportBatch`

| Column | Type | Null |
|---|---|---|
| `Id` | `uuid` PK | no |
| `ProjectId` | `uuid` FK Restrict → `Projects` | no |
| `CreatedById` | `uuid` | no |
| `Source` | `varchar(16)` | no |
| `Status` | `varchar(32)` | no |
| `OriginalFileName` | `varchar(200)` | no |
| `StorageKey` | `varchar(512)` | yes |
| `FileDeletedAt` | `timestamptz` | yes |
| `ExpiresAt` | `timestamptz` | no |
| `FallbackColumnId` | `uuid` | yes |
| `CreatedAt` | `timestamptz` | no |
| `HeadersJson` | `jsonb` | no |
| `SampleJson` | `jsonb` | yes |
| `SuggestedMappingJson` | `jsonb` | yes |
| `MappingJson` | `jsonb` | yes |
| `WarningsJson` | `jsonb` | yes |

`Status`: `uploaded` | `mapped` | `previewed` | `committed` | `undone` | `expired`.

`FallbackColumnId` has **no** FK. A staging row must not stop column delete (`BoardColumn` delete is a product path). The service checks the id.

`HeadersJson` is a JSON array of header strings. `SampleJson` is the first five data rows (cells cut to 200 characters) and is set null when the file is deleted. `SuggestedMappingJson` and `MappingJson` are the `ColumnMapping` object. `WarningsJson` is the batch-warning array. These exist so E5/E6 can render the wizard without re-reading the blob.

No `WorkspaceId`. The filter joins `Project`, same as `BoardColumn` (workspace match only; do not also exclude archived projects).

Index `IX_ImportBatches_ProjectId_CreatedAt` on `("ProjectId", "CreatedAt")`.

### `ImportBatchRow`

One row per data row. Header is not a row. `RowNumber` is 1-based among data rows.

| Column | Type | Null | Kept after the file is deleted |
|---|---|---|---|
| `Id` | `uuid` PK | no | yes |
| `ImportBatchId` | `uuid` FK Restrict → `ImportBatches` | no | yes |
| `RowNumber` | `int` | no | yes |
| `ExternalId` | `varchar(200)` | yes | yes |
| `ParentExternalId` | `varchar(200)` | yes | yes |
| `Title` | `varchar(500)` | yes | **nulled** |
| `Description` | `varchar(10000)` | yes | **nulled** |
| `ColumnName` | `varchar(100)` | yes | **nulled** |
| `AssigneeEmail` | `varchar(320)` | yes | **nulled** |
| `PriorityText` | `varchar(50)` | yes | **nulled** |
| `DueDateText` | `varchar(80)` | yes | **nulled** |
| `EstimateText` | `varchar(40)` | yes | **nulled** |
| `Status` | `varchar(32)` | no | yes |
| `ErrorCode` | `varchar(64)` | yes | yes |
| `WarningCode` | `varchar(64)` | yes | yes |
| `TaskId` | `uuid` FK Restrict → `TaskItems`, optional | yes | yes |

At most one blocking `ErrorCode` and one `WarningCode` per row (the first in A.4 order). Further warnings are dropped.

Unique `IX_ImportBatchRows_ImportBatchId_RowNumber` on `("ImportBatchId", "RowNumber")`.

Row `Status`: `pending` | `ready` | `warning` | `invalid` | `created` | `skipped_existing` | `skipped_archived` | `skipped_invalid` | `skipped_source_archived`.

### `ProjectExportLog`

| Column | Type | Null |
|---|---|---|
| `Id` | `uuid` PK | no |
| `ProjectId` | `uuid` FK Restrict → `Projects` | no |
| `ActorUserId` | `uuid` | no |
| `Format` | `varchar(8)` `csv` \| `json` | no |
| `RowCount` | `int` | no |
| `CreatedAt` | `timestamptz` | no |

Index `IX_ProjectExportLogs_ProjectId_CreatedAt` on `("ProjectId", "CreatedAt")`. No GET in v1. The row is the audit.

### Query filters

Add these only in `WorklaneDbContext.OnModelCreating`. One filter each. No filter in the `IEntityTypeConfiguration`.

- `ImportBatch`: tenant is set and `batch.Project.WorkspaceId == tenant`.
- `ImportBatchRow`: tenant is set and `row.ImportBatch.Project.WorkspaceId == tenant`.
- `ProjectExportLog`: tenant is set and `log.Project.WorkspaceId == tenant`.

`IgnoreQueryFilters` plus an explicit `Project.WorkspaceId` (and `ProjectId`) predicate, with a comment, only for:

1. The external-id lookup, because archived cards still occupy the unique index. A miss here would insert and then 500, or would hide `skipped_archived`.
2. The rate-limit partition's project→workspace read (same shape as `AttachmentRateLimitPartition`, which already ignores filters for the route id).

Do not use `IgnoreQueryFilters` to read another tenant's batch.

### FKs (all Restrict)

| FK | On delete |
|---|---|
| `ImportBatches.ProjectId` → `Projects` | Restrict |
| `ImportBatchRows.ImportBatchId` → `ImportBatches` | Restrict |
| `ImportBatchRows.TaskId` → `TaskItems` | Restrict |
| `TaskItems.ImportBatchId` → `ImportBatches` | Restrict |
| `ProjectExportLogs.ProjectId` → `Projects` | Restrict |

No FK to `AspNetUsers` (`CreatedById` / `ActorUserId` match `TaskItem.CreatedById`, which has none). No FK from `FallbackColumnId`.

Cascade on `Project → TaskItems` stays. A future project hard-delete (none exists; PDPA erasure is not in this tree) must, before deleting tasks: delete `ProjectExportLogs` and `ImportBatchRows` for the project, null `TaskItems.ImportBatchId` and `ParentTaskId`, then delete `ImportBatches`. A future card purge must set `ImportBatchRows.TaskId` to null before deleting the card (`ON DELETE RESTRICT` would otherwise block it). Do not add either path in this feature.

## A.2 Migration `AddTaskImport` (implementation PR only)

This document adds no migration and does not run one.

- **Up:** nullable columns on `TaskItems` → check → partial indexes → `ImportBatches` → `ImportBatchRows` → `ProjectExportLogs` → FKs Restrict. `ADD COLUMN` nullable does not rewrite `TaskItems`. No backfill (existing cards stay null).
- **Down:** drop FKs → new tables → partial indexes → check → the three columns. Down drops import history and external ids. Say that in the implementation PR.
- One migration in that PR. Generate with EF. Do not apply it to a shared database. Validate with `dotnet ef migrations script` and Testcontainers.

### Postgres errors that must not become 500

| Condition | Code |
|---|---|
| Unique violation on `IX_TaskItems_ProjectId_ImportSource_ExternalId` | 409 `import_external_id_taken` |
| Any other new unique violation | existing `unique_conflict` |

Register the index in `UniqueConstraintConflicts` and in `docs/api-error-codes.md` as `planned (WL-IMPORT)` until the API PR emits it. Parent-link SQLSTATE mapping is unchanged (`ParentLinkConflicts`).

## A.3 Presets and mapping

Targets (closed set). There is no `labels` target.

| Target | Card field | Required mapping |
|---|---|---|
| `title` | `Title` | exactly one column |
| `externalId` | `ExternalId` | exactly one column |
| `description` | `Description` | at most one |
| `column` | column name → `ColumnId` | at most one |
| `assigneeEmail` | workspace member email → `AssigneeId` | at most one |
| `priority` | `Priority` | at most one |
| `dueDate` | `DueDate` | at most one |
| `estimateDays` | `EstimateDays` | at most one |
| `parentExternalId` | pass-2 parent key | at most one |
| `ignore` | dropped | any number |

`fallbackColumnId` is required at mapping time and must be a column of **this** project. A column the tenant cannot see, or a column of another project, is 404 `not_found` (same body as a missing id). The user maps status text onto real columns; unmatched text uses the fallback and records warning `import_column_fallback`. Empty column cell uses the fallback with no warning. Two columns with the same name (allowed): the lower `Order` wins.

Match column cells to `BoardColumn.Name` by trim, case-insensitive, ordinal. A source status longer than 100 characters cannot match (column names are at most 100) and uses the fallback. Duplicate headers in the file: the second is renamed `Name (2)`, then `(3)`, and the suggestion leaves it unmapped until the user assigns it. Header match for presets is case-insensitive, ordinal, after trim. Store the file's header spelling.

**Not Worklane fields.** The preset suggests `ignore`, and E2 still requires an assignment. E1's `warnings` list the headers that suggestion sets to `ignore`. E2 replaces that list from the saved mapping. The headers are: labels, tags, vendor start/created/completed/updated timestamps, story points, time tracking, comments, and the other names in that preset's ignore list, plus any header the user moved to `ignore` from a suggested real target. Labels and tags are in the list because no label entity exists. Do not write a vendor start or completed time into `StartedAt` or `CompletedAt`; `WorkingDays` measures from `StartedAt`, so that would invent man-days. Story points and time-tracking values are not `EstimateDays`.

`StartedAt` and `CompletedAt` on a **created** card follow `NewTask`: set only from the destination column's `IsInProgress` / `IsDone` at commit time (`UtcNow`).

### Priority words → `TaskPriority`

Compare trim, case-insensitive. Empty cell → `Medium`, no warning.

| Words | Value |
|---|---|
| `urgent`, `highest`, `critical`, `blocker` | `Urgent` |
| `high`, `major` | `High` |
| `medium`, `normal` | `Medium` |
| `low`, `lowest`, `minor`, `trivial` | `Low` |

Anything else → `Medium` and warning `import_priority_defaulted`. The synonym list is an assumption; confirm with a sample (§C Q-7).

### Dates

Accept, in order: round-trip ISO-8601, `yyyy-MM-dd` (UTC midnight), `yyyy-MM-dd HH:mm` and `yyyy-MM-ddTHH:mm` (no offset means UTC), Excel date serials on `.xlsx` (1900 date system). **Assumption, verify with a sample:** Jira CSV datetimes of the form `d/MMM/yy`, `dd/MMM/yy`, and `ddd, d MMM yyyy HH:mm:ss zzz` (English month abbreviations only). Anything else, including a Thai-formatted date, is blocking `import_date_invalid`. Do not apply a user time zone (`AppUser` has `PreferredLanguage` only).

### Estimates

Empty → null. Otherwise invariant decimal, `> 0` and `<= 260` (same as `CreateTaskValidator`). Else blocking `import_estimate_invalid`. Do not convert hours or story points.

### Assignee

Trim. Empty → null, no warning. Otherwise look up `AppUser.NormalizedEmail` among **this** workspace's `WorkspaceMembers` only. No match (unknown account **or** account outside the workspace) → `AssigneeId` null and warning `import_assignee_skipped`. Same code either way: the row must not reveal whether the email exists. Do not emit `assignee_not_member` from import.

### Parent key

Trim. Empty → no parent. Equal to the row's own external id → blocking `parent_self`. Resolved in A.5. Comparison is case-sensitive.

### Preset `generic` (Worklane round-trip)

Suggested headers, case-insensitive: `Title`, `External Id`, `Description`, `Column`, `Assignee Email`, `Priority`, `Due Date`, `Estimate Days`, `Parent External Id`. `Display Key`, `Assignee Name`, `Parent Display Key`, `Started At`, `Completed At`, `Created At`, `Updated At` → `ignore` (display key is not an external id).

### Preset `jira`

Suggested map when the header exists. When `Parent key` exists, suggest it for `parentExternalId` and suggest `ignore` for `Parent`. When only `Parent` exists, suggest `Parent` → `parentExternalId`; the rule below then decides what those cells mean.

| Target | Headers, first match wins | Notes |
|---|---|---|
| `externalId` | `Issue key`, `Key` | Stable key such as `PROJ-123`. |
| `title` | `Summary` | |
| `description` | `Description` | |
| `column` | `Status` | |
| `priority` | `Priority` | synonym table |
| `dueDate` | `Due date`, `Due` | |
| `assigneeEmail` | `Assignee email`, `Assignee Email` | **Not** `Assignee`. Cloud CSV `Assignee` is a display name. Email is often absent. |
| `parentExternalId` | `Parent key`, `Parent Key` | |
| `ignore` | `Issue Type`, `Issue type`, `Labels`, `Reporter`, `Creator`, `Created`, `Updated`, `Resolved`, `Comment` (and `Comment` + digits) | |

**Assumption, verify with a sample file.** Atlassian documents CSV export of search results; the all-fields header row is not a single published list. Community reports disagree whether `Parent` is a key (`PROJ-1`) or a numeric issue id.

At preview and commit, if the mapping assigns a header named `Parent` (case-insensitive) to `parentExternalId`, the server rewrites each row's parent key as follows and records `import_parent_column_assumed` (`mode`: `key` | `issueId` | `unresolved`). A mapped `Parent key` column is already a key; this rule does not run.

1. If every non-empty `Parent` cell matches `^[A-Z][A-Z0-9]+-\d+$`, treat those cells as keys (`mode` `key`).
2. Else if an `Issue id` column exists and every non-empty `Parent` cell is all digits, join `Parent` to another row's `Issue id` and use that row's external id (`mode` `issueId`). A parent id that is not in the file → warning `import_parent_unresolved` (the card still commits, unlinked).
3. Else leave parent empty and warning `import_parent_unresolved` for each non-empty `Parent` (`mode` `unresolved`). Do not guess.

Jira can be three levels (epic → story → subtask). This product is two (`parent_depth_exceeded`: "A subtask can't have its own subtasks."). Do not flatten a subtask onto the epic. The deepest rows are blocking. Example: epic, story whose parent is the epic, subtask whose parent is the story → epic and story can commit; the subtask is `parent_depth_exceeded`.

### Preset `asana`

Header row: scan the first 10 rows for a cell equal to `Task ID` (case-insensitive). That row is the header. Rows above it are ignored. **Assumption:** some `.xlsx` exports put a title on row 1 and headers on row 3. The scan is the rule; confirm with a sample.

| Target | Headers | Notes |
|---|---|---|
| `externalId` | `Task ID` | Keep as text. A CSV opened in Excel may already have rounded a 16-digit id; the server does not repair that. |
| `title` | `Name` | |
| `description` | `Notes` | |
| `column` | `Section/Column`, `Section` | **Assumption:** Asana's older public column list has no section column. If the header is missing, every row uses the fallback. |
| `assigneeEmail` | `Assignee Email` | **Not** `Assignee` (display name). **Assumption:** email is in current exports and was absent from Asana's early column list. |
| `dueDate` | `Due Date` | |
| `priority` | `Priority` | Only when that header exists (custom field). **Assumption.** |
| `ignore` | `Assignee`, `Tags`, `Start Date`, `Created At`, `Creation Date`, `Completed At`, `Completion Date`, `Last Modified`, `Last Modified Date`, `Projects`, `Project Name`, `Blocked By (Dependencies)`, `Blocking (Dependencies)` | |

**Parent is a name, not an id.** **Assumption, supported by Asana's forum request for a parent id:** `Parent task` / `Parent Task` is the parent's name. Map it to `parentExternalId` only after this resolution, which writes the parent's `Task ID` into the child's `ParentExternalId`:

- Consider only rows whose own parent cell is empty (top-level) as candidate parents.
- Trim, case-sensitive match on `Name`.
- 0 matches → warning `import_parent_unresolved`, child commits with no parent.
- 2 or more matches → blocking `import_parent_ambiguous`.
- 1 match → child's parent external id becomes that row's `Task ID`.

Do not treat a non-empty `Parent task` as an id, even if it looks numeric.

### Preset `trello`

Trello's own docs: JSON export on every plan; CSV on Premium. The CSV **column names are not listed on the official export page**. The map below is an **assumption to verify with a Premium CSV sample**. Until a sample is attached, BE ships this map and a test fixture checked into the API PR that uses these headers; a sample that differs is a follow-up, not a silent remap.

| Target | Header | Notes |
|---|---|---|
| `externalId` | `Card ID` | |
| `title` | `Card Name` | |
| `description` | `Card Description` | |
| `column` | `List Name` | |
| `dueDate` | `Due Date` | |
| `ignore` | `Labels`, `Members`, `Card URL`, `Attachment Count`, `Checklists`, `Date Last Activity` | `Members` are usernames, not emails. No priority and no parent column. |

If a header `Archived` or `Closed` exists and the cell is `true`, `yes`, `1`, or `archived` (case-insensitive), the row status is `skipped_source_archived` (not an error, not a card).

### File shape

- `.csv` and `.xlsx` only. Extension decides the type, not the client content type. `.xlsx` must be a zip (`PK` signature, same bytes as `FileSignature` for `.xlsx`). `.csv` must decode as text the way `FileSignature` accepts text: UTF-8, UTF-16 with BOM, or TIS-620/windows-874. Strip a UTF-8 BOM.
- CSV delimiter is comma. If the header line has zero commas and at least one semicolon, 400 `import_delimiter_unsupported`. No other delimiter.
- `.xlsx`: first worksheet only. Further sheets → batch warning `import_extra_sheets` (`sheetCount`). Do not evaluate formulas. A formula cell with a cached value uses the cache; with no cache, blocking `import_formula_uncached` on that row.
- Reject uncompressed size over `Imports:MaxUncompressedBytes` or more than 64 zip entries: 400 `import_unreadable`.
- More than `Imports:MaxColumns` headers: 400 `import_column_limit`. More than `Imports:MaxRows` data rows: 400 `import_row_limit`. Header only, or zero data rows: 400 `import_no_rows`.
- Cell text is trimmed. Title empty or whitespace → blocking `import_title_missing`. Title longer than 500 → blocking `import_title_too_long`, and the stored `Title` is the first 500 characters. Description longer than 10_000 → blocking `import_description_too_long`, stored as the first 10_000 characters. Upload accepts the file either way; preview reports the row. The row DTO's `title` is that stored prefix.

Upload response includes `headers`, the first 5 data rows (each cell a string or JSON `null`, each cell cut to 200 characters for the sample only), and `suggestedMapping`. The full cell is what preview and commit use.

## A.4 Row evaluation order

Same function for preview and commit. First blocking code wins. Then the first warning, if the row is not blocking.

1. Source row skipped (`skipped_source_archived`) — stop.
2. `import_external_id_missing` — empty external id.
3. `import_external_id_duplicate` — a later row in this file repeats an external id. The first row continues.
4. `import_title_missing`, `import_title_too_long` (`maxLength` 500), `import_description_too_long` (`maxLength` 10000).
5. `import_date_invalid`, `import_estimate_invalid`, `import_formula_uncached`.
6. Existing card in this project with the same source + external id, **including archived** (ignore filters, tenant + project predicate): active → row `skipped_existing`; archived → `skipped_archived`. Do not read its title into the response beyond `displayKey` and `taskId`. Do not update it. Parent cell on a skipped row is ignored.
7. Parent rules for a row that will be created, against the file graph plus existing active cards of this project and source:
   - `parent_self`
   - `import_parent_ambiguous` (Asana name clash)
   - `parent_depth_exceeded` — parent is itself a child (in the file or in the DB), or the chain is longer than one
   - `parent_child_limit` — active children already on that parent, plus earlier rows in this file that will link to it, would exceed `Tasks:MaxChildrenPerParent`. Extension `maxChildren`.
   - `child_has_children` does not apply to new cards. It is not emitted on the skip path (existing cards are not re-parented).
8. Warnings, first only: `import_parent_unresolved`, else `import_assignee_skipped`, else `import_column_fallback`, else `import_priority_defaulted`.

`import_parent_unresolved` still creates the card with `ParentTaskId` null. It does not make the row invalid.

Preview persists those statuses (`ready` = no warning and no error, `warning`, `invalid`, plus the skip statuses) and sets the batch to `previewed`. It creates no cards, no activity, no SignalR.

## A.5 Commit (two passes, one transaction)

Preconditions, else no writes:

| State | Result |
|---|---|
| Batch expired | 409 `import_expired`. Delete the file if it is still stored. Status `expired`. |
| Status `uploaded` | 409 `import_mapping_required` |
| Status `committed` | 409 `import_already_committed` |
| Status `undone` or `expired` | 409 `import_expired` for expired; 409 `import_already_undone` for undone |
| Project has no columns | 409 `conflict`, detail `Project has no columns. Create a column first.` |
| Fallback column not in this project | 409 `import_column_missing` |
| Any blocking row and `skipInvalidRows` is false | 409 `import_has_errors` (`invalidCount`). Status is unchanged. No cards. |
| `skipInvalidRows` true | Blocking rows become `skipped_invalid`. They are not inserted. |

Commit is allowed from `mapped` or `previewed`. It always re-runs A.4 inside the transaction before pass 1, so a skipped or stale preview cannot commit a row that is now blocking.

`SELECT` the batch `FOR UPDATE` at the start. A second commit waits, then sees `committed` and returns 409 `import_already_committed`.

**Pass 1.** Walk rows in `RowNumber` order. `skipped_existing`, `skipped_archived`, `skipped_invalid`, and `skipped_source_archived` insert nothing. `ready` and `warning` insert a card. Creates: `NextTaskNumberAsync` SQL (extract the statement; do not add LINQ after `SqlQuery`; do not call the private method), order `max + 65536` within the destination column, `NewTask` field rules, `ImportSource`, `ExternalId`, `ImportBatchId`, `ParentTaskId` null. Activity `Created` with the existing JSON plus `source: "import"`, `importBatchId`, `externalId`. `UpdatedAt = CreatedAt`.

**Pass 2.** For created rows that have a resolved parent external id and no blocking parent code: resolve the parent to a card created in pass 1 or an existing **active** card in this project with the same source and external id. Lock the parent and child rows `ORDER BY "Id"` (`BoardService` parent locks). Re-check depth and the cap inside the lock. On success set `ParentTaskId` and write `ChildAdded` / `ParentSet` with `source: "import"` and `importBatchId` (extend the current `"manual"` value; do not use `"manual"` for these rows). Then set both cards' `UpdatedAt` back to their `CreatedAt` so pass 2 is not an edit. On a lock-time rule failure: if `skipInvalidRows` is false, roll the **whole** transaction back and return 409 `import_has_errors`; if true, leave `ParentTaskId` null, keep the card, set the row warning to that parent code, and continue.

Build `ImportCommitDto` (including row `title`) before clearing storage. Then null the sensitive row columns in A.1, set `SampleJson` null, `IFileStorage.DeleteAsync` on the storage key, set `StorageKey` null and `FileDeletedAt`, status `committed`, one `SaveChanges`. A later E5 has `title` null and `sampleRows` []. Broadcast only after commit (A.11).

Command timeout for this transaction: `Imports:CommitCommandTimeoutSeconds` (60). On that timeout, roll back, leave status `previewed`, return 409 `import_timeout`. A client disconnect is not this code.

A 23505 on the external-id index rolls back and returns 409 `import_external_id_taken` (also mapped in middleware if it escapes). The client runs preview again and commits again; the winner's cards are then `skipped_existing`.

Idempotent means: a second **file** imported with the same source and external ids does not create a second card. It does not mean a second commit call on the same batch returns 200.

## A.6 Undo

`POST …/undo` requires status `committed`. Otherwise 409 `import_not_committed` (still `uploaded` / `mapped` / `previewed`), `import_already_undone`, or `import_expired`.

Load cards with `ImportBatchId` = this batch, ignore the archived filter, explicit workspace predicate.

A card is **pristine** only when all of these hold:

- `IsArchived` is false
- no `TaskComment` and no `TaskAttachment`
- every `TaskActivity` is one of `Created` (exactly one), `ParentSet` (at most one), `ChildAdded` (any), and each of those JSON objects has `source` `"import"` and `importBatchId` equal to this batch
- `UpdatedAt` is within 1 ms of `CreatedAt` (pass 2 must not leave `UpdatedAt` newer)

Otherwise it was edited. Archive is not a new activity type (none exists). Undo does not insert activity.

Compute the archive set:

1. Already archived → result reason `already_archived`, not archived again.
2. Not pristine → `edited_after_import`, kept.
3. Pristine, but it has an active child that is **not** in the archive set → `has_active_children`, kept. Re-evaluate until stable so a kept child also keeps that parent.
4. Remaining pristine cards → `IsArchived = true` in one transaction.

Do not clear `ParentTaskId` (WL-PARENT: archive keeps the link). Do not delete storage attachments (a pristine card has none). Do not un-archive. Restore is out of scope (no restore API on `develop`).

Status `undone`. Second call: 409 `import_already_undone`. Broadcast after commit (A.11).

## A.7 Endpoints

Controller `ImportsController`, `[Authorize]`, `[Route("api")]`. Validators named `<Request>Validator`, constructor-injected. Success JSON uses the app's camelCase options and string enums.

| # | Method + route | Role | Limiter | Success |
|---|---|---|---|---|
| E1 | `POST /api/projects/{projectId}/imports` | Member | `import-upload` | **201** `ImportBatchDto` |
| E2 | `PUT /api/projects/{projectId}/imports/{importId}/mapping` | Member | `import-preview` | **200** `ImportBatchDto` |
| E3 | `POST /api/projects/{projectId}/imports/{importId}/preview` | Member | `import-preview` | **200** `ImportPreviewDto` |
| E4 | `POST /api/projects/{projectId}/imports/{importId}/commit` | Member | `import-commit` | **200** `ImportCommitDto` |
| E5 | `GET /api/projects/{projectId}/imports/{importId}` | Viewer | none | **200** `ImportPreviewDto` |
| E6 | `GET /api/projects/{projectId}/imports` | Viewer | none | **200** `ImportBatchDto[]`, `CreatedAt` desc |
| E7 | `POST /api/projects/{projectId}/imports/{importId}/undo` | Admin | `import-commit` | **200** `ImportUndoDto` |
| E8 | `GET /api/projects/{projectId}/tasks/export?format=csv` or `json` | Viewer | `export` | **200** file |

E1 is `multipart/form-data`, field `file`, field `preset` (`generic`|`jira`|`asana`|`trello`). Read the form in the action (`TaskAttachmentsController` does this) so a size failure is not model-state 400. `[RequestSizeLimit]` = `Imports:MaxBytes` + `FileStorageSettings.UploadEnvelopeBytes` (1 MiB). Empty file: 400 `file_empty`. Over the per-file cap after the body is accepted: 413 `file_too_large` with `maxBytes`. Bad extension or signature: 415 `unsupported_file_type` with `extension` (leading dot, or empty string when there is none).

E1 also: 409 `import_batch_limit` when this project already has `Imports:MaxOpenBatchesPerProject` open batches (`limit`). 409 `conflict` when the project has no columns. On any 4xx after the bytes were stored, delete the storage key before returning. A rejected upload does not leave a batch.

E2 body is `UpdateImportMappingRequest`. 400 `validation_failed` when a header is unknown, a header is missing, a header is listed twice, a target is unknown, `title` or `externalId` is missing or duplicated, any other single-value target is duplicated, or the body contains `preset`. Every file header appears exactly once. The source is fixed at E1. Sets status `mapped`. Allowed from `uploaded`, `mapped`, or `previewed` (a new map clears preview row results back to `pending`). Not from `committed` / `undone` / `expired` (409 as in A.5).

E3 has no body. Requires `mapped` or `previewed`.

E5 returns `ImportPreviewDto` for any status that is not `expired` (rows may still be `pending` before E3). An expired batch is 409 `import_expired`. E6 returns one `ImportBatchDto` per batch: `headers` is `[]`, `sampleRows` is `[]`, `suggestedMapping` is null, `mapping` is present once E2 has succeeded, `warnings` is the stored array. E6 does not include rows.

E8 `format` is required. Missing or other value: 400 `validation_failed` on `Format`. Active cards only (query filter). Order: column `Order`, then card `Order`, then `Number`. Zero cards is 200, not an error. Write one `ProjectExportLog` in the same request (after the query, before the body is fully flushed is fine; a failed query writes nothing). Response headers: `Content-Disposition: attachment; filename="{projectKey}-tasks.csv"` (or `.json`), `X-Content-Type-Options: nosniff`. CSV `Content-Type: text/csv; charset=utf-8`. JSON `application/json; charset=utf-8`. Do not store the file.

Pipeline order on every call (first match wins), from `docs/conventions-api.md`: 401 (empty bearer challenge, or `unauthorized`), 429, 413 `request_too_large` when `Content-Length` is over the route limit, 400 `invalid_request_body`, 400 `invalid_route_id`, 404 `not_found` (missing project, non-member, or batch not in this project — same body), 403 `workspace_access_denied`, 400 `validation_failed`, then the service codes in A.9. `POST` upload checks 413 `file_too_large` and 415 only after that role gate.

## A.8 JSON shapes

Omitted JSON members are absent. `null` is a present nullable. Times are ISO-8601 with offset, UTC. Ids are UUID strings. Integers are JSON numbers. Booleans are JSON booleans.

### `ImportBatchDto` (E1, E2, E6 item)

```json
{
  "id": "3f0c8a2e-7b1d-4e6f-9a0b-1c2d3e4f5a6b",
  "projectId": "3f0c8a2e-7b1d-4e6f-9a0b-1c2d3e4f5a6b",
  "source": "jira",
  "status": "uploaded",
  "originalFileName": "issues.csv",
  "expiresAt": "2026-11-05T10:00:00.0000000+00:00",
  "createdAt": "2026-11-04T10:00:00.0000000+00:00",
  "createdById": "3f0c8a2e-7b1d-4e6f-9a0b-1c2d3e4f5a6b",
  "fallbackColumnId": null,
  "headers": ["Summary", "Issue key"],
  "sampleRows": [["Fix login", "PROJ-1"]],
  "suggestedMapping": { "assignments": [{ "header": "Summary", "target": "title" }] },
  "mapping": null,
  "warnings": [{ "code": "import_field_ignored", "headers": ["Labels"] }]
}
```

| Field | Type | Null |
|---|---|---|
| `id`, `projectId`, `createdById` | string (uuid) | no |
| `source` | string enum | no |
| `status` | string enum | no |
| `originalFileName` | string | no |
| `expiresAt`, `createdAt` | string (date-time) | no |
| `fallbackColumnId` | string (uuid) | yes |
| `headers` | string[] | no; empty only on E6 |
| `sampleRows` | array of (string or null)[] | no; empty on E6 and after file delete |
| `suggestedMapping` | `ColumnMapping` | yes on E6 |
| `mapping` | `ColumnMapping` | yes until E2 |
| `warnings` | `BatchWarning[]` | no; may be empty |

E6 omits `sampleRows` contents and `suggestedMapping` (null). It still sends `headers: []` and `mapping` when status is past `uploaded`.

`ColumnMapping`: `{ "assignments": [ { "header": string, "target": string } ] }`. Both strings non-null. `fallbackColumnId` is **not** inside this object; it is a sibling on the batch and on the E2 body.

`BatchWarning`: `code` string, non-null. Include `headers` (string array) only for `import_field_ignored`, `sheetCount` (integer) only for `import_extra_sheets`, and `mode` (string) only for `import_parent_column_assumed`. Omit the others.

### E2 `UpdateImportMappingRequest`

```json
{
  "fallbackColumnId": "3f0c8a2e-7b1d-4e6f-9a0b-1c2d3e4f5a6b",
  "assignments": [{ "header": "Summary", "target": "title" }]
}
```

`fallbackColumnId` string (uuid), required, not null. `assignments` required. One element per file header, each header once. `target` is the closed set in A.3.

### `ImportPreviewDto` (E3, E5)

`batch` is `ImportBatchDto`. `sampleRows` stays available until the file is deleted (commit, undo, or expiry), including while status is `previewed`. `rows` is `ImportRowDto[]` in `rowNumber` order.

`ImportRowDto`:

| Field | Type | Null |
|---|---|---|
| `rowNumber` | integer ≥ 1 | no |
| `title` | string | yes after the file is deleted; otherwise the stored title (prefix if it was too long) |
| `externalId` | string | yes |
| `status` | string enum | no |
| `taskId` | string (uuid) | yes until created or skipped-existing |
| `displayKey` | string | yes until a card id is known |
| `columnId` | string (uuid) | yes when the row will not be created |
| `columnName` | string | yes when `columnId` is null |
| `assigneeId` | string (uuid) | yes |
| `parentExternalId` | string | yes |
| `error` | `RowProblem` | yes |
| `warning` | `RowProblem` | yes |

`RowProblem`: `{ "code": string, "maxLength": integer|null, "maxChildren": integer|null }`. Only the extension that code uses is non-null.

### E4 `CommitImportRequest` / `ImportCommitDto`

```json
{ "skipInvalidRows": false }
```

`skipInvalidRows` boolean, required. JSON null → 400 `validation_failed` (`not_nullable`).

```json
{
  "batchId": "3f0c8a2e-7b1d-4e6f-9a0b-1c2d3e4f5a6b",
  "status": "committed",
  "createdCount": 1,
  "skippedExistingCount": 0,
  "skippedArchivedCount": 0,
  "skippedInvalidCount": 0,
  "skippedSourceArchivedCount": 0,
  "linkedCount": 0,
  "unlinkedCount": 0,
  "rows": []
}
```

Counts are integers ≥ 0, non-null. `unlinkedCount` is created cards whose parent was requested but left null. `rows` is the same `ImportRowDto` list as preview, with final statuses (`created`, skip statuses).

### E7 `ImportUndoDto`

```json
{
  "batchId": "3f0c8a2e-7b1d-4e6f-9a0b-1c2d3e4f5a6b",
  "status": "undone",
  "archivedCount": 1,
  "kept": [{ "taskId": "3f0c8a2e-7b1d-4e6f-9a0b-1c2d3e4f5a6b", "displayKey": "WL-14", "reason": "edited_after_import" }]
}
```

`reason`: `edited_after_import` | `has_active_children` | `already_archived`. `kept` may be empty. `archivedCount` does not include kept rows.

### E8 export fields

CSV: UTF-8 with BOM, comma, CRLF, RFC 4180 quoting. Header row, exactly, in this order:

`External Id,Title,Description,Column,Assignee Email,Priority,Due Date,Estimate Days,Parent External Id,Parent Display Key,Display Key,Assignee Name,Started At,Completed At,Created At,Updated At`

A value that starts with `=`, `+`, `-`, `@`, tab, or CR is prefixed with `'` so a spreadsheet does not treat it as a formula. JSON does not get that prefix.

JSON body:

```json
{
  "projectId": "3f0c8a2e-7b1d-4e6f-9a0b-1c2d3e4f5a6b",
  "projectKey": "WL",
  "exportedAt": "2026-11-04T10:00:00.0000000+00:00",
  "tasks": [
    {
      "externalId": null,
      "title": "Fix login",
      "description": null,
      "column": "To Do",
      "assigneeEmail": null,
      "assigneeName": null,
      "priority": "Medium",
      "dueDate": null,
      "estimateDays": null,
      "parentExternalId": null,
      "parentDisplayKey": null,
      "displayKey": "WL-14",
      "startedAt": null,
      "completedAt": null,
      "createdAt": "2026-11-04T10:00:00.0000000+00:00",
      "updatedAt": "2026-11-04T10:00:00.0000000+00:00"
    }
  ]
}
```

| JSON field | Type | Null |
|---|---|---|
| `projectId` | string (uuid) | no |
| `projectKey` | string | no |
| `exportedAt` | string (date-time) | no |
| `tasks` | array | no |
| `title`, `column`, `priority`, `displayKey`, `createdAt`, `updatedAt` | string | no |
| `externalId`, `description`, `assigneeEmail`, `assigneeName`, `dueDate`, `parentExternalId`, `parentDisplayKey`, `startedAt`, `completedAt` | string | yes |
| `estimateDays` | number | yes |
| `priority` | `Low` \| `Medium` \| `High` \| `Urgent` | no |

`assigneeEmail` is included for Viewer as well: that role can already read emails from the members endpoint. `parentExternalId` is the parent's `ExternalId` (null when the parent was not imported). `parentDisplayKey` is set whenever `ParentTaskId` is set. `estimateDays` is the stored man-day number, not a story point.

Generic preset maps this CSV back, except the columns listed as `ignore` in A.3.

## A.9 Errors

HTTP bodies are `application/problem+json`. New `detail` sentences below are the catalog text. Add every new HTTP code to `docs/api-error-codes.md` in the API PR.

| Code | HTTP | Extensions | When |
|---|---|---|---|
| `file_empty` | 400 | | E1 file missing or 0 bytes. Existing sentence. |
| `import_no_rows` | 400 | | Header only, or no data rows. Detail: `The file has no data rows.` |
| `import_delimiter_unsupported` | 400 | | CSV header has no comma and has a semicolon. Detail: `Save the file as comma-separated CSV.` |
| `import_unreadable` | 400 | | Workbook/text cannot be parsed, or uncompressed cap / entry cap exceeded. Detail: `The file could not be read.` |
| `import_row_limit` | 400 | `maxRows` | Data rows exceed the cap. Detail: `The file has more than {maxRows} rows.` |
| `import_column_limit` | 400 | `maxColumns` | Headers exceed the cap. Detail: `The file has more than {maxColumns} columns.` |
| `validation_failed` | 400 | `errors`, `fieldCodes` | E2/E4/E8 validator. Existing. |
| `invalid_request_body` | 400 | | Body does not bind. Existing. |
| `unsupported_file_type` | 415 | `extension` | Not `.csv` / `.xlsx`, or signature mismatch. Existing. |
| `request_too_large` | 413 | | `Content-Length` over the route ceiling. No `maxBytes`. Existing. |
| `file_too_large` | 413 | `maxBytes` | File over `Imports:MaxBytes`. Existing sentence. |
| `not_found` | 404 | | Non-member, missing project, batch not in this project, fallback column not visible at E2. Existing. |
| `workspace_access_denied` | 403 | | Viewer on E1–E4; Member on E7. Existing. |
| `unauthorized` | 401 | | Empty user id. Existing. Bearer failure is an empty body. |
| `rate_limited` | 429 | `scope` = `user` | Over the policy in A.10. `Retry-After` header. No limits in the body. Existing. |
| `conflict` | 409 | | Project has no columns. Existing detail from `BoardService`. |
| `import_batch_limit` | 409 | `limit` | Too many open batches. Detail: `This project already has {limit} imports in progress.` |
| `import_mapping_required` | 409 | | Preview or commit before a mapping. Detail: `Map the columns before continuing.` |
| `import_has_errors` | 409 | `invalidCount` | Commit blocked. Detail: `The import has {invalidCount} row(s) to fix or skip.` |
| `import_column_missing` | 409 | | Fallback column was deleted after mapping. Detail: `The fallback column is no longer in this project.` |
| `import_already_committed` | 409 | | Second commit. Detail: `This import was already committed.` |
| `import_already_undone` | 409 | | Second undo, or commit after undo. Detail: `This import was already undone.` |
| `import_not_committed` | 409 | | Undo before commit. Detail: `Commit the import before undoing it.` |
| `import_expired` | 409 | | Past `ExpiresAt` and not committed. Detail: `This import has expired.` |
| `import_timeout` | 409 | | Commit command timeout; transaction rolled back. Detail: `The import took too long. Try again.` |
| `import_external_id_taken` | 409 | | Unique index race. Detail: `A card with this external id already exists for this project and source.` |
| `internal_error` | 500 | `traceId` | Anything else. Existing. |

Row codes (preview/commit JSON, not the HTTP `code`). The Web app localizes them the same way. Put them in the catalog with HTTP `—` and status `planned (WL-IMPORT)` so they are not mistaken for response codes.

| Code | Kind | Extensions | When |
|---|---|---|---|
| `import_external_id_missing` | blocking | | Blank external id. |
| `import_external_id_duplicate` | blocking | | Repeated external id in the file (not the first). |
| `import_title_missing` | blocking | | Blank title. |
| `import_title_too_long` | blocking | `maxLength` | Title over 500. |
| `import_description_too_long` | blocking | `maxLength` | Description over 10_000. |
| `import_date_invalid` | blocking | | Due date not in A.3. |
| `import_estimate_invalid` | blocking | | Estimate empty-not, non-numeric, or outside (0, 260]. |
| `import_formula_uncached` | blocking | | Formula cell with no cached value. |
| `import_parent_ambiguous` | blocking | | Asana parent name matches more than one top-level row. |
| `parent_self` | blocking | | Parent key equals this external id. Existing detail. |
| `parent_depth_exceeded` | blocking | | Parent is already a child, or the file chain is deeper than one. Existing detail. |
| `parent_child_limit` | blocking | `maxChildren` | Cap would be exceeded. Existing detail. |
| `import_parent_unresolved` | warning | | Parent key/name/id not found. Card is created unlinked. |
| `import_assignee_skipped` | warning | | Email not a member of this workspace, or no such account. Same code. |
| `import_column_fallback` | warning | | Status/list text matched no column. |
| `import_priority_defaulted` | warning | | Unknown priority word; stored `Medium`. |
| `import_field_ignored` | batch warning | `headers` | Unmapped non-fields (labels, tags, vendor timestamps, story points). |
| `import_extra_sheets` | batch warning | `sheetCount` | Workbook has more than one sheet. |
| `import_parent_column_assumed` | batch warning | `mode` | Jira `Parent` column rule in A.3. |

WIP over the limit does not add a code. Create already allows it (`IsOverWipLimit` on the card DTO only).

## A.10 Limits, storage, PDPA, time

| Key | Default |
|---|---|
| `Imports:MaxBytes` | 5_242_880 (5 MiB) |
| `Imports:MaxRows` | 2000 |
| `Imports:MaxColumns` | 64 |
| `Imports:MaxUncompressedBytes` | 20_971_520 (20 MiB) |
| `Imports:RetentionHours` | 24 |
| `Imports:MaxOpenBatchesPerProject` | 10 |
| `Imports:CommitCommandTimeoutSeconds` | 60 |
| `Imports:RealtimeFanoutLimit` | 50 |
| `RateLimit:ImportUpload` | 10 per 3600 s |
| `RateLimit:ImportCommit` | 10 per 3600 s (commit and undo share it) |
| `RateLimit:ImportPreview` | 60 per 3600 s (mapping and preview) |
| `RateLimit:Export` | 30 per 3600 s |

Partition key: JWT `sub` + workspace id of the route `projectId` (explicit tenant predicate). Body `scope` stays `user`. `Program.cs` `RateLimitScope` emits `user` for every policy except auth; do not add a new scope string. A user in workspace A does not consume workspace B, because the key includes the workspace. 429 is before the action.

Sync: E1–E8 run on the request. There is no commit queue. Over the row cap is 400, not a job.

Storage: `IFileStorage` key generated by the server, prefix `imports/{batchId:N}/`. Never a client path. Same local root as attachments (`FileStorage:LocalPath`) but **no** `TaskAttachment` row and **not** counted in `FileStorage:WorkspaceQuotaBytes`. `OriginalFileName` is the file name only: drop directories and control characters, max 200.

Delete the blob with `DeleteAsync`:

- immediately after a successful commit (A.5), and on undo if a key is still set
- when a request returns 4xx after a temp write
- when a batch is touched after `ExpiresAt` (status becomes `expired` if it was not committed)
- from `ImportRetentionService`, an `IHostedService` every 15 minutes, for batches with a non-null `StorageKey` whose `ExpiresAt` is past or whose status is `committed` or `undone`

The same moments null the sensitive `ImportBatchRow` columns. Batch header (who, when, file name, source, status, counts via the remaining row statuses) stays. Export bytes are not stored; `ProjectExportLog` is the record (actor, format, row count, time). No email and no card title in that log.

The hosted service is the only background work. It has no endpoint.

## A.11 Realtime

Group `project:{projectId}` (`NotificationsHub.GroupName`). After the DB commit, never before. Another project's group gets nothing. The hub already checks membership on join.

| When | Event | Payload |
|---|---|---|
| Commit, `createdCount` ≤ fan-out limit | existing `TaskCreated` per created card, then `TaskUpdated` for each parent whose counts changed, then `ImportCompleted` | `TaskCreated` / `TaskUpdated` are `TaskItemDto`. `ImportCompleted`: `{ projectId, importId, createdCount, skippedCount, refetch: false }` |
| Commit, over the limit | `ImportCompleted` only | `refetch: true`, `createdCount`, `skippedCount` |
| Undo, `archivedCount` ≤ limit | `ImportUndone` | `{ projectId, importId, archivedTaskIds: string[], refetch: false }` |
| Undo, over the limit | `ImportUndone` | `archivedTaskIds: []`, `refetch: true` |

Do not send `TaskArchived` (the notifier does not have it). Upload, map, preview, and export send nothing. Add `ImportCompletedAsync` and `ImportUndoneAsync` to `IBoardNotifier`.

`Created` / `ParentSet` / `ChildAdded` details for this feature include `source: "import"` and `importBatchId`. Existing manual links stay `source: "manual"`.

## A.12 Roles

| Action | Minimum `WorkspaceRole` |
|---|---|
| E1–E4 | Member |
| E5, E6, E8 | Viewer |
| E7 | Admin |

Owner passes Admin. The attribute enforces this. No extra membership check in the action. A Member who imported cannot undo (Q-1).

## A.13 Audit

- Batch row is the import audit: actor, time, source, file name, per-row status and error code. Cell text and assignee email are removed when the file is deleted.
- Card audit is `TaskActivity` as in A.5 and A.11, same transaction as the card.
- Export audit is `ProjectExportLog` only.

## A.14 BE acceptance

Tests under `tests/Worklane.IntegrationTests/Imports/`. `ReadFromJsonAsync` uses `TestAuthHelper.JsonOptions`.

1. Migration Up/Down from EF; script and snapshot check; nullable add; Down drops only import data. Cross-project parent FK still fails. External-id unique index rejects a second active **or** archived row. 23505 on that index is 409 `import_external_id_taken`, never 500.
2. Non-member on E1 and E5: 404 `not_found`, body equal to a missing project id. Viewer on E1: 403 `workspace_access_denied`. Member on E7: 403. Viewer on E8: 200.
3. Batch id from another project in the same workspace, with this `projectId` in the route: 404.
4. E1 `.csv` / `.xlsx` preset `generic`: 201, suggested map contains `title` and `externalId`. `.xls` and a renamed zip: 415 `unsupported_file_type`. 0-byte: 400 `file_empty`. Over `MaxBytes`: 413 `file_too_large` with `maxBytes`. `Content-Length` over the route ceiling: 413 `request_too_large` and no `maxBytes`. Semicolon header: 400 `import_delimiter_unsupported`. 2001 rows: 400 `import_row_limit`.
5. Eleventh open batch: 409 `import_batch_limit`. Project with no columns: 409 `conflict` and the existing sentence.
6. E2 without title or external id: 400 `validation_failed`. Fallback column in another project: 404. E3 before E2: 409 `import_mapping_required`.
7. Preview: one row per data row; codes in A.4 with the listed extensions; no `TaskItems` written.
8. Commit `skipInvalidRows: false` with one blocking row: 409 `import_has_errors`, zero new cards. `true`: blocking rows `skipped_invalid`, valid rows created.
9. Pass 2: child `parentTaskId` set, parent `childCount` + 1, activity `source` `import`. A third level: `parent_depth_exceeded`. 51st child: `parent_child_limit`. Unresolved parent: card exists, `parentTaskId` null, warning `import_parent_unresolved`.
10. Second file, same source and external id: `skipped_existing`, no field changes, no new activity. Archived match: `skipped_archived`, no new card. Same id under `generic` and `jira` creates two cards (source is part of the key).
11. Assignee email of a non-member and of an unknown address: both `import_assignee_skipped`, `assigneeId` null, responses byte-identical on that row. Member email assigns.
12. `Priority` `Low` is stored as Low (sentinel). Due date `yyyy-MM-dd` is UTC midnight. Column `IsDone` sets `CompletedAt`. A "Start Date" header is not written to `StartedAt`.
13. File key is gone after commit and after expiry. Row `Title` and `AssigneeEmail` are null after that. `ProjectExportLog` and batch status remain. Bytes are not in `TaskAttachments` and do not change the quota sum.
14. Undo archives only pristine cards. A card with a comment, an attachment, a `Moved`/`Updated` activity, or `UpdatedAt` newer than `CreatedAt` is `edited_after_import`. A pristine parent whose child was edited is `has_active_children` and stays active. Second undo: 409 `import_already_undone`.
15. SignalR: a client joined to another project gets no event. Under the fan-out limit, `TaskCreated` count equals `createdCount`, and `ImportCompleted.refetch` is false. Over the limit, only `ImportCompleted` with `refetch` true.
16. Export CSV header equals A.8, BOM present, a title `=1+1` is prefixed with `'`. JSON has no prefix, `tasks` in column/order, archived cards absent. Viewer response includes `assigneeEmail` for an assigned member. Each export inserts one log row.
17. 429 `rate_limited`, `scope` `user`, `Retry-After` set, no limit numbers in the body, on the upload policy.
18. Low priority, parent cap, and `NextTaskNumberAsync` still follow the existing traps (no `FirstAsync` on the returning query).

## A.15 BE tickets

Implementation PRs go to `develop` and stay unmerged until the owner merges. One migration, in BE-1's PR only. API PR lands before FE.

| # | Ticket | md | Depends on | Done when |
|---|---|---|---|---|
| BE-1 | Entities, configurations, the one `HasQueryFilter` each, migration Up/Down, check, indexes, Restrict FKs, `UniqueConstraintConflicts`, catalog rows `planned`, options | 1.5 | — | AC 1 |
| BE-2 | E1–E2, presets, parse, storage, retention fields, batch cap, no-columns `conflict` | 2.5 | BE-1 | AC 2–6, and the storage half of AC 13 for a failed upload |
| BE-3 | E3–E6, both passes, idempotency, activity `source` `import`, SignalR, sensitive-column nulling, file delete on commit | 3.0 | BE-2 | AC 7–12, 15, and commit half of AC 13 |
| BE-4 | E7 undo rules | 1.25 | BE-3 | AC 14 |
| BE-5 | E8 CSV and JSON, formula prefix, `ProjectExportLog` | 1.25 | BE-1 | AC 16. Can be developed beside BE-2. |
| BE-6 | `ImportRetentionService` every 15 minutes | 0.75 | BE-2 | Expired open batches lose the blob and the sensitive columns without a client call. AC 13 expiry. |
| BE-7 | Rate-limit policies and the 429 test | 0.5 | BE-2 | AC 17 |

**BE total: 10.75 md.** BE-5 does not need the commit path. FE still waits for the contract, which is this document, and for a deployed or mocked API before it merges.

---

# Part B — Frontend (Worklane-Web)

Angular. No endpoint other than A.7. Dates on screen use the app's existing Thai/English date formatting; the file and the API stay ISO, Gregorian. Layouts: 390 px and 1440 px.

## B.1 Where

Project board overflow:

- `import.open` — visible for Member and above. Hidden for Viewer.
- `export.csv` and `export.json` — visible for Viewer and above. Disabled while a download is in flight.

Import is a four-step wizard (dialog or route under the project; back does not lose the server batch). `import.history` lists E6 and is visible to Viewer; the undo control inside it is visible only to Admin.

## B.2 Wizard

**Step 1 — Upload.** Preset radios: generic, Jira, Asana, Trello (`import.preset.*`). File input accept `.csv,.xlsx`. Submit → E1.

States: idle; uploading (controls disabled); error (problem `code`, file kept locally so the user can retry); success → step 2.

**Step 2 — Map.** One row per header: sample cell, target dropdown (A.3 targets plus ignore). Prefill `suggestedMapping`; the user can change every row. Fallback column dropdown is required (project columns from the board already loaded; if the column list is empty, show `import.noColumns` and do not call E2). Unmapped headers sit on `ignore`. Labels/tags have no target; the dropdown does not contain "Labels". Helper `import.labelsUnsupported` when a header is `Labels` or `Tags`. Submit → E2 then E3, go to step 3.

States: idle; saving (disabled); `validation_failed` inline on the control; 404 on the column → `not_found` and reload columns.

**Step 3 — Preview.** Table: row number, title, external id, column name, error or warning in the user's language, status. Checkbox `import.skipInvalid` bound to `skipInvalidRows`, default **off**. Primary button commits (E4). If `invalidCount` would be > 0 and the box is off, the button stays enabled and the API is the source of the 409 (do not hide the error). Batch warnings (`import_field_ignored`, `import_extra_sheets`, `import_parent_column_assumed`) sit above the table.

States: loading skeleton; empty (should not happen after E1); error with retry (re-calls E3); committing (disabled, no double submit).

**Step 4 — Result.** Counts from `ImportCommitDto`. Link to the board. Admin sees `import.undo`. Member does not. Undo confirm: `import.undoTitle` / `import.undoBody`. Success lists `kept` with the reason code. `ImportCompleted` / `ImportUndone`: if `refetch` is true, reload the board; if false, apply `TaskCreated` / `TaskUpdated` as today and remove ids in `archivedTaskIds`.

**Expired:** any 409 `import_expired` returns the wizard to step 1 with `import.expired`.

**History:** E6 rows show file name, source, status, created time. Opening a `previewed` batch loads E5 into step 3. `committed` opens step 4 data from E5 (counts derived from row statuses). Viewer does not see commit or undo.

## B.3 i18n

Keys in `public/i18n/en.json` and `th.json`. Parity test must pass. Error text goes through the existing `apiError` map by `code`, including row codes.

| Key | TH | EN |
|---|---|---|
| `import.open` | นำเข้า | Import |
| `import.preset.generic` | ไฟล์ Worklane | Worklane file |
| `import.preset.jira` | Jira | Jira |
| `import.preset.asana` | Asana | Asana |
| `import.preset.trello` | Trello | Trello |
| `import.drop` | วางไฟล์ CSV หรือ XLSX | Drop a CSV or XLSX file |
| `import.noColumns` | สร้างคอลัมน์ก่อนนำเข้า | Create a column before importing. |
| `import.labelsUnsupported` | โปรเจกต์นี้ยังไม่มีป้ายกำกับ คอลัมน์นี้จะไม่ถูกนำเข้า | This project has no labels. This column will not be imported. |
| `import.fallback` | คอลัมน์เมื่อจับสถานะไม่ได้ | Column when status does not match |
| `import.skipInvalid` | ข้ามแถวที่ผิดพลาด | Skip rows with errors |
| `import.undo` | เลิกทำการนำเข้า | Undo import |
| `import.undoTitle` | เลิกทำการนำเข้านี้? | Undo this import? |
| `import.undoBody` | การ์ดที่ยังไม่ถูกแก้ไขจะถูกเก็บถาวร การ์ดที่แก้แล้วจะคงอยู่ | Cards that were not edited will be archived. Edited cards stay. |
| `import.expired` | ไฟล์นำเข้าหมดอายุแล้ว อัปโหลดใหม่ | This import has expired. Upload it again. |
| `import.history` | ประวัตินำเข้า | Import history |
| `import.reason.edited_after_import` | มีการแก้ไขหลังนำเข้า | Edited after import |
| `import.reason.has_active_children` | ยังมีงานย่อยที่ใช้งานอยู่ | Still has active subtasks |
| `import.reason.already_archived` | เก็บถาวรไปแล้ว | Already archived |
| `export.csv` | ส่งออก CSV | Export CSV |
| `export.json` | ส่งออก JSON | Export JSON |

Row and HTTP codes from A.9 use `apiError`, not a second table, except the three undo reasons above (they are not problem codes).

## B.4 FE acceptance

1. Viewer sees export and history, and does not see Import or Undo. Member sees Import and not Undo. Admin sees Undo on the result and on a committed history row.
2. Preset plus file calls E1 once. Suggested mapping is applied and remains editable. Commit sends the edited mapping, not the suggestion, after E2.
3. Preview renders each row status. `skipInvalidRows` defaults to false and is sent on E4.
4. 409 `import_has_errors` shows the catalog sentence and stays on step 3. 409 `import_expired` returns to step 1.
5. Result counts match the commit body. Undo confirm calls E7 once; kept rows show the TH or EN reason.
6. `ImportCompleted` with `refetch: true` reloads the board and does not require one event per card. With `refetch: false`, new cards appear.
7. Every new `code` in A.9 has a TH and an EN string (Vitest). en/th key parity passes.
8. Export downloads the file name `{key}-tasks.csv` or `.json` and does not navigate away. The button is disabled until the response returns.
9. 390 px and 1440 px: wizard steps stack or fit without a clipped primary button.

## B.5 FE tickets

| # | Ticket | md | Depends on | Done when |
|---|---|---|---|---|
| FE-1 | Wizard steps 1–3, mapping, preview, i18n, error map | 2.5 | Contract in A (mock until BE-3) | B.4 items 2, 3, 4, 7 |
| FE-2 | Result, history, undo, SignalR | 1.25 | FE-1, BE-3, BE-4 | B.4 items 1, 5, 6 |
| FE-3 | Export menu | 0.5 | BE-5 | B.4 items 1, 8 |

**FE total: 4.25 md.** FE-3 can start from the E8 contract before undo exists. Do not merge FE before the API PR that implements the same contract.

---

# C. Out of scope, questions, totals

**Out of scope, v1:** AI mapping, MCP, Trello JSON, creating columns from unknown status values, a labels model, writing vendor start/completed times into `StartedAt`/`CompletedAt`, story points as man-days, comments and attachments from the file, updating cards on re-import, linking across projects, a third parent level, semicolon or tab CSV, archived-card export, reading `ProjectExportLog` from the client, undoing an undo, a background commit worker.

**Defaults below are the contract.** A different answer changes only the row cited.

| # | Default | If the owner decides otherwise |
|---|---|---|
| Q-1 | Only Admin can undo | Member who created the batch may undo while every card is pristine: role check moves into the service and needs a new 403 (not `workspace_access_denied`, which the attribute already uses for non-admins). |
| Q-2 | Jira depth over 1 is `parent_depth_exceeded` | Flatten the deepest card onto the top card: new warning `import_parent_flattened`, about +0.5 md. |
| Q-3 | Unknown parent is a warning; the card is created unlinked | Make it blocking: it joins A.4 step 7 and blocks commit when skip is false. |
| Q-4 | Unknown assignee is a warning and the card is unassigned | Make it blocking. Still one code for unknown and non-member. |
| Q-5 | Do not create columns | Creating a column per unknown status is a separate ticket (Member, name max 100, `IsDone` false). |
| Q-6 | Re-import skips the existing card | Field update of pristine cards is a new pass and changes undo. |
| Q-7 | Preset headers and the priority synonyms in A.3, including every row marked assumption | Replace the assumed header after a sample file is attached. Trello's whole header row is unverified. Jira `Parent` as key vs issue id is unverified. Asana `Parent task` as a name, `Assignee Email`, and `Section/Column` need a sample. |
| Q-8 | Undo sets `IsArchived`. It does not delete. | Hard-delete of pristine cards must clear `ParentTaskId` first (self-FK Restrict) and is a different permission story. |
| Q-9 | Export includes `assigneeEmail` for Viewer | Stripping it for Viewer breaks generic round-trip of assignees for that role. Members list would still show the email. |
| Q-10 | Comma-only CSV | Semicolon support is a parser change, about +0.25 md. |

**Totals:** BE 10.75 md + FE 4.25 md. QA is not a ticket in this spec: A.14 and B.4 are the test lists.

**This PR** is the spec only. It does not add a migration, a package, or an endpoint.
