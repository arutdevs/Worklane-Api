# WL-PARENT — Parent/child tasks (subtasks): implementation spec

- **Status:** Final contract for BE and FE · open product questions awaiting อรุษ (§C)
- **Sources:** SA design `parent-subtask-design.md` v1.1 (2 Nov 2026) · PO spec `parent-subtask-spec.md` v1.1 · `docs/api-error-codes.md`
- **Code refs:** `arutdevs/Worklane-Api` `develop` @ `aa0add0` (verified via GitHub). Web file names are from the SA code map and were **not** re-verified on Web `develop`; FE checks them before starting.
- **Order:** Part A (BE) first; Part B (FE) builds on the contract in A.3–A.5 and can mock it before BE merges.

## 0. Scope

**In (v1):** a task has at most one optional parent; **one level only** (a child cannot have children; a task with children cannot become a child); parent and child must be in the **same project** (so the same workspace); no self-parent, no cycles; parent detail shows children + progress `done/total`; child shows a link to its parent; set / change / clear parent on create and edit; defined archive / purge behaviour.

**Out:** checklists, card types, more than one level, cross-project links, auto-moving the parent when all children are done, weighted progress, bulk re-parenting, move-to-another-project (no endpoint exists; if added later it returns 409 `has_parent_or_children`).

**Definitions**
- *Active* = `IsArchived = false`.
- *Done child* = active child whose column has `IsDone = true`.
- `childCount` = active children; `doneChildCount` = done children. Archived children count toward neither.
- Cap: `Tasks:MaxChildrenPerParent` = **50** active children (config, not code).

---

# Part A — Backend (Worklane-Api)

## A.1 Data model

`TaskItem` (`src/Worklane.Domain/Entities/TaskItem.cs:5-45`) has no `WorkspaceId`; workspace comes from `Project`. So the link is tied through `ProjectId`, which is stricter (same project ⇒ same workspace).

| Item | Definition |
|---|---|
| Column | `ParentTaskId uuid NULL` · entity `Guid? ParentTaskId`, navigations `Parent`, `Children` |
| Alternate key | unique `AK_TaskItems_ProjectId_Id` on `(ProjectId, Id)` (exists only so the FK can reference it) |
| FK | `FK_TaskItems_TaskItems_ProjectId_ParentTaskId`: `(ProjectId, ParentTaskId)` → `TaskItems(ProjectId, Id)`, **`DeleteBehavior.Restrict`** (`ON DELETE RESTRICT`). PG MATCH SIMPLE: rows with `ParentTaskId NULL` are not checked. |
| CHECK | `CK_TaskItems_NotSelfParent`: `"ParentTaskId" IS NULL OR "ParentTaskId" <> "Id"` |
| Index | partial `IX_TaskItems_ProjectId_ParentTaskId` on `(ProjectId, ParentTaskId) WHERE "ParentTaskId" IS NOT NULL` (also serves the RESTRICT lookup) |
| Depth | Enforced in the service under row locks (A.6). Optional defense in depth (decision pending, Q-2): trigger `BEFORE INSERT OR UPDATE OF "ParentTaskId"` that raises if the parent has a parent or the row has children; its SQLSTATE maps to 409 `parent_depth_exceeded`, never 500. |
| Cycles | Impossible given depth 1 + "a task with children can't be a child"; self-loop blocked by CHECK. |

**Activity types** (append to `TaskActivityType.cs:8-18` — stored as string, never insert in the middle; if WL-56 lands first, append after its `Archived`): `ChildAdded`, `ChildRemoved` (on parent), `ParentSet`, `ParentCleared` (on child). `Details = { otherTaskId, otherDisplayKey, source }`, `source` ∈ `"manual" | "ai-proposal"`, set by the server. Written in the same transaction as the link change.

## A.2 Migration `AddTaskParent` (code only)

⚠️ Generated with EF as code in the PR. **Never run against any real database.** Validation is `dotnet ef migrations script` + snapshot check in CI and Testcontainers tests.

- **Up:** add column → AK unique index → partial index → composite FK (Restrict) → CHECK → (trigger function + trigger, only if Q-2 accepted).
- **Down (reverse):** drop trigger/function → CHECK → FK → indexes → column. Down loses only the link data; state this in the PR.
- **Backfill:** none (all rows NULL). `ADD COLUMN NULL` does not rewrite the table.
- **Ordering:** if WL-56 (`AddTaskArchivedAt`) or WL-73 migrations are open, rebase the timestamp before merge.
- **Hard delete path:** `Project → TaskItems` is Cascade (`TaskItemConfiguration.cs:33-36`); a hard delete of a project would hit the self-FK RESTRICT. If any project/workspace hard-delete path exists (incl. PDPA erasure WL-67), it must first run `UPDATE "TaskItems" SET "ParentTaskId" = NULL WHERE "ProjectId" = @p`. Not yet confirmed whether such a path exists on develop (risk R-1) — BE-1 confirms and tests.

## A.3 DTO changes

`TaskItemDto` (`src/Worklane.Application/Board/Dtos/BoardDtos.cs:20-68`) — append to the record; every endpoint that returns `TaskItemDto` (board list, detail, create, update, move, link) includes them:

| JSON field | Type | Null | Meaning |
|---|---|---|---|
| `parentTaskId` | string (uuid) | nullable | parent id; `null` if no parent |
| `parentDisplayKey` | string | nullable | parent key e.g. `"WL-12"`; `null` iff `parentTaskId` is `null` |
| `childCount` | integer ≥ 0 | non-null | active children |
| `doneChildCount` | integer, 0..childCount | non-null | active children in an `IsDone` column |

Counts are computed server-side; the board list uses **one grouped query** per project (group by `ParentTaskId`), no N+1.

`CreateTaskRequest` (`BoardDtos.cs:79-87`) — append `Guid? ParentTaskId`:
```json
{ "...existing fields...": "...", "parentTaskId": "3f0c…-uuid" }   // optional; omitted or null = no parent
```

`UpdateTaskRequest` (PATCH, `BoardDtos.cs:89-97`) gets **no** parent field. Parent changes go only through A.4. BE-1 confirms how an unknown `parentTaskId` in a PATCH body is handled (ignored vs rejected depending on `UnmappedMemberHandling`) and pins it with a test; FE never sends it.

## A.4 Endpoints

All responses ≥ 400 are `application/problem+json` with stable `code` (existing contract). Role filter = `[RequireWorkspaceRole]`.

| # | Method + route | Role (resource) | Request | Success |
|---|---|---|---|---|
| E1 | `POST /api/projects/{projectId}/tasks` | Member (Project) | `CreateTaskRequest` + optional `parentTaskId` | **201** child `TaskItemDto` |
| E2 | `PUT /api/tasks/{taskId}/parent` | Member (Task; `taskId` = child) | `{ "parentTaskId": "uuid" }` — required, non-null | **200** child `TaskItemDto` |
| E3 | `DELETE /api/tasks/{taskId}/parent` | Member (Task) | none | **200** child `TaskItemDto` (`parentTaskId: null`) |
| E4 | `GET /api/tasks/{taskId}/children` | Viewer (Task) | none | **200** `TaskItemDto[]`, active children only, ordered `CreatedAt` asc then `Number` asc; `[]` if none |
| E5 | `DELETE /api/tasks/{taskId}?includeChildren=true` | Member (Task) | none | **204**; archives the task and all its active children (see A.7) |

Idempotency:
- E2 with the **same** current parent → 200, no write, no activity, no broadcast.
- E2 with a **different** parent while one is set → 409 `already_has_parent` (no silent re-parent; client must E3 first).
- E3 when no parent → 200 no-op.

"Change parent" = E3 then E2. "Clear parent" = E3.

## A.5 Errors (final)

Evaluation order (agreed order: 401 > 429 user > 413 > 404 routing > 400 binding > 404 non-member > 403 role > 400 validator > 429 workspace_* > rule 409s):
1. Binding/validation: missing/invalid `parentTaskId` on E2 → 400 `invalid_request_body` / `validation_failed` (existing codes).
2. `parent_self` (400) — route id = body id; checked before any DB read.
3. Parent not visible (missing, or other workspace) → 404 `not_found`, body **byte-identical** to a random-id 404. Every 404 is checked before every 409 (no existence oracle).
4. Rule 409s below, evaluated under the row lock.

| Code | Status | `params` | When | Endpoints |
|---|---|---|---|---|
| `parent_self` | 400 | – | parent id = child id | E2 (E1 n/a) |
| `not_found` | 404 | – | parent id missing or in another workspace; or caller not a member | E1, E2, E3, E4, E5 |
| `workspace_access_denied` | 403 | – | Viewer calls a Member endpoint | E1, E2, E3, E5 |
| `parent_different_project` | 409 | – | parent in another project of the same workspace | E1, E2 |
| `parent_depth_exceeded` | 409 | – | parent is itself a child | E1, E2 |
| `child_has_children` | 409 | `childCount` | the task being linked has children | E2 |
| `parent_child_limit` | 409 | `maxChildren` | parent already has 50 active children | E1, E2 |
| `already_has_parent` | 409 | `parentDisplayKey` | child already has a different parent | E2 |
| `card_archived` | 409 | – | parent or child is archived (same workspace) | E1, E2 |
| `parent_has_active_children` | 409 | `childCount` | archive a task with active children without `includeChildren=true` | `DELETE /api/tasks/{id}` |
| `parent_has_children` | 409 | `childCount` | purge a task that has any child (active or archived) | WL-58 purge |
| `parent_archived` | 409 | – | restore a child whose parent is archived | restore (feature 3-#1, not WL-56) |
| `parent_link_conflict` | 409 | – | deadlock / serialization failure / lock timeout (40P01, 40001) | E1, E2, E3, E5 |

DB exception mapping: FK `23503` on the self-FK → 409 `parent_has_children`; CHECK `23514` on `CK_TaskItems_NotSelfParent` → 400 `parent_self`; trigger (if any) → 409 `parent_depth_exceeded`. None may surface as 500.

Add all new codes to `docs/api-error-codes.md` as `planned (WL-PARENT)`, switch to `emitted` when shipped.

## A.6 Concurrency

One transaction (Read Committed) for E1-with-parent, E2, E3, E5:
1. `SELECT … FROM "TaskItems" WHERE "Id" IN (@parent, @child) ORDER BY "Id" FOR UPDATE` (E1: parent row only; lock order by Id prevents deadlocks).
2. Check rules (archived, depth, child_has_children, already_has_parent, cap = `COUNT(*) WHERE ParentTaskId=@parent AND NOT IsArchived`).
3. Write + activity rows, commit. Broadcast **after commit** only.

Covers: two users adding the 50th child at once; A→C while C→D at the same time (both lock C). Not SERIALIZABLE (would need a retry loop).

## A.7 Archive / restore / purge

- **Archive child:** link kept; child leaves the parent's counts.
- **Archive parent with active children:** without `includeChildren=true` → 409 `parent_has_active_children`. With it → parent + all active children archived in one transaction (parent row locked); already-archived children are skipped (no 404); if WL-56 is merged, set `ArchivedAt = now` on each.
- **Restore** (feature 3-#1, outside Phase 0): child whose parent is archived → 409 `parent_archived`; restore also checks the cap. Restoring a parent does not restore children.
- **Purge** (WL-58 `DELETE /api/tasks/{id}/purge`, Admin): parent with any child → 409 `parent_has_children`, checked **after** WL-58's "archive first" 409 and **before** any storage file is deleted. Purging a child is normal; parent activity keeps `otherDisplayKey` for display.
- **Moves:** moving a parent between columns never moves children and vice versa. No auto-move of the parent.

## A.8 Tenant isolation

- Filter resolves workspace from the route task (`BoardService.cs:510-517`); non-member → 404.
- Parent from the body is read through the tenant query filter (`WorklaneDbContext.cs:69-72`) → other workspace = 404.
- The archived check needs `IgnoreQueryFilters()` **plus** explicit `t.Project.WorkspaceId == tenant` (as in `BoardService.cs:564-565`); otherwise an archived parent becomes 404 instead of 409 `card_archived`. `IgnoreQueryFilters()` without the tenant predicate is a review blocker.
- DB composite FK makes cross-project links impossible even with a service bug.

## A.9 Realtime (SignalR, project group — `SignalRBoardNotifier.cs:41`)

| Action | Events |
|---|---|
| Link / unlink | `TaskUpdated`(child) + `TaskUpdated`(parent, new counts) |
| Create child | `TaskCreated`(child) + `TaskUpdated`(parent) |
| Child moved into/out of done column | `TaskMoved`(child) + `TaskUpdated`(parent) |
| Archive child | `TaskArchived`(child) + `TaskUpdated`(parent) |
| Archive with children | `TaskArchived` per task |
| Purge | WL-58 `TaskPurged { projectId, taskId }` only |

`TaskArchived { projectId, taskId, columnId }` is defined by WL-56; WL-PARENT **appends** `parentTaskId: string|null`. `IBoardNotifier` has no `TaskArchivedAsync` on develop yet (`IBoardNotifier.cs:12-22`); WL-56 adds it.

## A.10 BE acceptance criteria

1. Migration Up/Down generated by EF; `migrations script` passes in CI; snapshot matches; no backfill. Testcontainers: cross-project FK insert fails; self-parent CHECK fails; Down is clean.
2. E1 with valid `parentTaskId` → 201, `parentTaskId`/`parentDisplayKey` set, parent `childCount` +1.
3. E2 valid → 200; repeat with same parent → 200, no new activity/broadcast. E3 → 200 `parentTaskId: null`; repeat → 200.
4. Each row of A.5 is produced by an integration test with the listed status, code and params.
5. PUT with another-workspace parent id → 404 body byte-identical to random guid. Outsider on E2/E3/E4 → 404. Viewer on E1/E2/E3/E5 → 403; Viewer on E4 → 200.
6. Archived parent in same workspace → 409 `card_archived`; archived parent in other workspace → 404.
7. Race: parent with 49 active children, two concurrent links → exactly one 200, the other 409 `parent_child_limit` or `parent_link_conflict`.
8. Counts: 5 active children, 2 in done columns → `childCount 5`, `doneChildCount 2`; archiving one done child → `4/1`. Board list has no N+1 (one grouped query).
9. Archive parent with active children without flag → 409 `parent_has_active_children` `childCount`; with flag → all archived, 204.
10. Purge parent with an archived child → 409 `parent_has_children`, no storage file deleted (after WL-58).
11. Activity rows `ChildAdded`/`ParentSet` and `ChildRemoved`/`ParentCleared` written with `source`.
12. SignalR: client in another project receives no event; parent receives `TaskUpdated` with new counts.
13. FK 23503 / CHECK 23514 never return 500.

## A.11 BE tickets

| # | Ticket | md | Depends on |
|---|---|---|---|
| BE-1 | Migration + domain: column, AK, FK, CHECK, index, (trigger), config, DB error mapping, 4 activity types, error-code rows `planned`, hard-delete unlink if path exists | 1.25 | – |
| BE-2 | E1–E4, locking, activity, broadcast, DTO fields + grouped counts | 2.0 | BE-1 |
| BE-3 | Archive with children (E5), `parent_has_active_children`, `ArchivedAt`, `TaskArchived.parentTaskId` | 0.75 | BE-2, **WL-56** |
| BE-4a | Purge rule `parent_has_children` + FK mapping | 0.25 | BE-3, **WL-58** |
| BE-4b | Restore rule `parent_archived` + cap on restore | 0.25 | feature 3-#1 (outside Phase 0) |

If WL-56 is late, BE-3 can ship `includeChildren` + `parent_has_active_children` first; broadcast / `ArchivedAt` follow after rebase.

---

# Part B — Frontend (Worklane-Web, Angular)

## B.1 Model

`models/board.ts` task model gains: `parentTaskId: string | null`, `parentDisplayKey: string | null`, `childCount: number`, `doneChildCount: number`.

API service methods: `setParent(taskId, parentTaskId)` → E2, `clearParent(taskId)` → E3, `getChildren(taskId)` → E4, create task with optional `parentTaskId` → E1, `archive(taskId, includeChildren?)` → E5.

## B.2 UI

**Create task form**
- Optional "Parent" picker: searches cards of the **same project** (existing `GET …/tasks?search=`). Excludes the task itself, cards that already have a parent, and archived cards. Clearable. Value sent as `parentTaskId` (omit when empty).
- "Add subtask" from a parent's detail opens create with the parent pre-filled.

**Task detail — child (has `parentTaskId`)**
- Shows `task.subtasks.parentOf` ("Subtask of WL-12") as a link to the parent.
- Edit: "Change parent" (picker) and "Clear parent" (`task.subtasks.unlink`). Change = `clearParent` then `setParent`; on failure of `setParent`, reload the task and show the error.

**Task detail — "Subtasks" section** (`task.subtasks.title`)
- Shown on cards with no parent. Hidden on child cards (one level).
- Progress: `task.subtasks.progress` "`{done}/{total}` done", aria label `task.subtasks.a11yProgress`.
- Rows: key, title, column name (as stored), assignee; order as returned by E4. Each row: open link + `unlink` action.
- Actions: `add` (create child), `linkExisting` (picker as above).
- States:
  - *Loading:* skeleton rows; actions disabled.
  - *Empty:* `task.subtasks.empty`; actions enabled.
  - *Error loading E4:* inline error (`apiError` by code) + retry.
  - *Disabled:* `add` and `linkExisting` disabled when user is Viewer, task is archived, or `childCount ≥ 50` (tooltip = `parent_child_limit` message). Unlink hidden for Viewer. Buttons disabled while a request is in flight (no double submit).

**Board tile**
- Chip `done/total` only when `childCount > 0` (PO default, Q-A-5).
- Child tile shows "↳ {parentDisplayKey}", clickable to the parent.

**Archive dialog**
- Archiving a task with `childCount > 0` opens a confirm: title `archiveConfirmTitle`, body `archiveConfirmBody`, buttons `archiveAll` / Cancel. Confirm → E5 with `includeChildren=true`.
- If the plain archive returns 409 `parent_has_active_children` (stale count), open the same dialog with `params.childCount`.

**Realtime:** apply `TaskUpdated` counts and parent fields; `TaskArchived` removes the tile and, when `parentTaskId` is set, reloads/decrements that parent's counts. Target ≤ 2 s.

## B.3 Error → message mapping

Use the existing `apiError` mapping by `code`; interpolate `params`. On `not_found` for a picked parent, show generic not-found and refresh the picker. On `parent_link_conflict`, show the message and reload the task.

| Code | TH | EN |
|---|---|---|
| `parent_self` | การ์ดเป็นงานย่อยของตัวเองไม่ได้ | A card can't be its own subtask. |
| `parent_different_project` | เชื่อมได้เฉพาะการ์ดในโปรเจกต์เดียวกัน | You can only link cards in the same project. |
| `parent_depth_exceeded` | งานย่อยมีงานย่อยซ้อนอีกชั้นไม่ได้ | A subtask can't have its own subtasks. |
| `child_has_children` | การ์ดนี้มีงานย่อยอยู่แล้ว จึงเป็นงานย่อยของการ์ดอื่นไม่ได้ | This card has subtasks, so it can't become a subtask. |
| `parent_child_limit` | การ์ดนี้มีงานย่อยครบ {{maxChildren}} รายการแล้ว | This card already has the maximum of {{maxChildren}} subtasks. |
| `already_has_parent` | การ์ดนี้เป็นงานย่อยของ {{parentDisplayKey}} อยู่แล้ว ยกเลิกการเชื่อมก่อน | This card is already a subtask of {{parentDisplayKey}}. Unlink it first. |
| `card_archived` | การ์ดที่เก็บถาวรแล้วเชื่อมไม่ได้ | Archived cards can't be linked. |
| `parent_has_active_children` | การ์ดนี้มีงานย่อยที่ยังใช้งาน {{childCount}} รายการ | This card has {{childCount}} active subtasks. |
| `parent_archived` | กู้คืนการ์ดหลักก่อน | Restore the parent card first. |
| `parent_has_children` | ลบงานย่อยหรือยกเลิกการเชื่อมก่อนลบถาวร | Delete or unlink the subtasks before deleting this card permanently. |
| `parent_link_conflict` | มีคนแก้การ์ดนี้พร้อมกัน โหลดใหม่แล้วลองอีกครั้ง | Someone changed this card at the same time. Reload and try again. |

## B.4 i18n keys (`public/i18n/en.json`, `th.json`, under `task.subtasks.*`)

| Key | TH | EN |
|---|---|---|
| `title` | งานย่อย | Subtasks |
| `progress` | เสร็จ {{done}}/{{total}} | {{done}}/{{total}} done |
| `add` | เพิ่มงานย่อย | Add subtask |
| `linkExisting` | เชื่อมการ์ดที่มีอยู่ | Link existing card |
| `unlink` | ยกเลิกการเชื่อม | Unlink |
| `empty` | ยังไม่มีงานย่อย | No subtasks yet |
| `parentOf` | งานย่อยของ {{key}} | Subtask of {{key}} |
| `archiveConfirmTitle` | เก็บถาวรการ์ดนี้และงานย่อย {{count}} รายการ? | Archive this card and its {{count}} subtasks? |
| `archiveConfirmBody` | งานย่อยจะถูกเก็บถาวรไปด้วย กู้คืนได้ภายหลัง | The subtasks will be archived too. You can restore them later. |
| `archiveAll` | เก็บถาวรทั้งหมด | Archive all |
| `a11yProgress` | งานย่อยเสร็จ {{done}} จาก {{total}} | {{done}} of {{total}} subtasks done |

## B.5 FE acceptance criteria

1. Create with a parent picked → request contains `parentTaskId`; new child appears in the parent's list; progress `0/1`.
2. Detail of a parent with 5 children (2 done) shows "2/5 done" and 5 rows in E4 order.
3. Empty, loading and error states render as in B.2; retry re-calls E4.
4. Child detail shows "Subtask of {key}" linking to the parent; "Clear parent" calls E3 and removes it; "Change parent" calls E3 then E2.
5. Parent picker never lists the task itself, archived cards, or cards that have a parent; link-existing picker also excludes cards with children.
6. Viewer: no add / link / unlink / change / clear controls; list and progress visible.
7. At 50 children, add/link disabled with the limit message.
8. Every code in B.3 shows its TH and EN message with params interpolated (Vitest).
9. i18n parity test passes (same keys in en/th).
10. Archive of a task with children opens the dialog; confirm sends `includeChildren=true`; a 409 `parent_has_active_children` also opens it.
11. Board chip shown only when `childCount > 0`; child tile shows "↳ key"; realtime count change visible ≤ 2 s.
12. Layouts checked at 390 px and 1440 px.

## B.6 FE tickets

| # | Ticket | md | Depends on |
|---|---|---|---|
| FE-1 | Subtasks section, parent picker on create/edit, parent link, clear/change, error mapping, i18n | 2.0 | BE-2 (can mock contract first) |
| FE-2 | Board chip, parent ref, realtime | 1.25 | BE-2, BE-3 |
| FE-3 | Archive-with-children dialog | 0.5 | BE-3 |

---

# C. Dependencies, estimate, open questions

**Dependencies:** BE-1, BE-2, FE-1 have none outside this spec. BE-3/FE-3 need **WL-56** (`ArchivedAt`, `TaskArchived`, `Archived` activity). BE-4a needs **WL-58** (purge; WL-58 itself depends on WL-56). BE-4b goes with restore feature 3-#1 (outside Phase 0). Neither WL-56 nor WL-58 is on `develop` @ `aa0add0`. AI F2 subtasks (PO AC 24–25) reuse the BE-2 service method and ship with F2.

**Order:** BE-1 → BE-2 → FE-1 → BE-3 (after WL-56) → (FE-2 ∥ FE-3) → BE-4a (after WL-58) → QA. BE-4b with 3-#1.

**Estimate:** BE 4.5 + FE 3.75 + QA 1.5 (test plan + E2E, outsider/Viewer/race) = **9.75 md**; before beta **9.5 md** (excludes BE-4b).

**Product questions — PO defaults used, awaiting อรุษ:**

| # | Default (awaiting อรุษ) | If answered differently |
|---|---|---|
| Q-A-1 | One level only | Multi-level: cycle check via recursive CTE under lock, new 409 `parent_cycle`, rollup counts, ≈ +1.5 md BE |
| Q-A-2 | Archive parent = confirm and archive all | "Block": drop `includeChildren` and FE-3 (−0.75 md). "Auto-unlink": archive sets children's parent to NULL + `ParentCleared` |
| Q-A-3 | Cap 50 per parent | Config change only |
| Q-A-4 | No Subtask type; rules are type-agnostic | Types before beta: migration + `parent_type_not_allowed` 409, ≈ +2–3 md |
| Q-A-5 | Progress chip only on cards with children | FE-2 condition only; no API change |

**Technical open items:** Q-2 (Security/Lead) accept the depth trigger (cost inside BE-1). R-1 project/workspace hard-delete path unconfirmed. R-7 Web file names not re-verified on `develop`.
