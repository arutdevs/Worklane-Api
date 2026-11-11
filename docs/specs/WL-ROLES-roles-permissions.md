# WL-ROLES — Roles and permissions (owner review)

- **Status:** Draft for อรุษ. Docs only. No code in this change.
- **Branch:** `develop` @ `4da7d9a`.
- **Unblocks:** the role-plan dependency in WL-RESTORE §7. It does not start WL-RESTORE.
- **Names:** the only role type in application code is `WorkspaceRole`: `Viewer`, `Member`, `Admin`, `Owner` (`src/Worklane.Domain/Enums/WorkspaceRole.cs:3-8`).

A higher value includes every lower minimum. The filter denies when `role < minimum` (`src/Worklane.WebApi/Filters/RequireWorkspaceRoleAttribute.cs:103-106`). `Admin` and `Owner` pass every `Member` check. `Owner` passes every `Admin` check.

**Proposed** means a recommendation in this doc, not a decision and not shipped behaviour.

---

## 1. Current state

### 1.1 The only role check on routes

`[RequireWorkspaceRole]` (`RequireWorkspaceRoleAttribute.cs:18-111`) reads the id from the route, never the body. Order inside the filter:

1. Empty user id → 401 `unauthorized` (`:40-44`).
2. Route id missing or not a guid → 400 `invalid_route_id` (`:47-51`).
3. Project, column, task, comment, or attachment id does not resolve → 404 `not_found` (`:86-89`). Workspace routes use the route id as the workspace id (`:55-57`).
4. No membership row → 404 `not_found`, same body as a missing id (`:97-100`).
5. Member whose role is below the minimum → 403 `workspace_access_denied` (`:103-106`).
6. On success, pin the tenant (`:109`).

Task, column, comment, and attachment lookups use `IgnoreQueryFilters()` (`BoardService.cs:475-490`, `TaskCommentService.cs:185-191`, `TaskAttachmentService.cs:249-255`). An archived card still resolves a workspace, so the filter can 404 a non-member or 403 a low role before the action runs. The action's own query filter still hides archived cards (`WorklaneDbContext.cs:69-72`).

Every attribute usage:

| Route | Minimum | Resource | Line |
|---|---|---|---|
| `GET /api/projects/{projectId}/tasks` | Viewer | Project | `TasksController.cs:27` |
| `POST /api/projects/{projectId}/tasks` | Member | Project | `TasksController.cs:41` |
| `GET /api/tasks/{taskId}` | Viewer | Task | `TasksController.cs:55` |
| `PATCH /api/tasks/{taskId}` | Member | Task | `TasksController.cs:64` |
| `POST /api/tasks/{taskId}/move` | Member | Task | `TasksController.cs:81` |
| `GET /api/workspaces/{workspaceId}/my-tasks` | Viewer | Workspace | `TasksController.cs:95` |
| `PUT /api/tasks/{taskId}/parent` | Member | Task | `TasksController.cs:104` |
| `DELETE /api/tasks/{taskId}/parent` | Member | Task | `TasksController.cs:118` |
| `GET /api/tasks/{taskId}/children` | Viewer | Task | `TasksController.cs:127` |
| `DELETE /api/tasks/{taskId}` (archive) | Member | Task | `TasksController.cs:139` |
| `GET /api/workspaces/{id}/projects` | Viewer | Workspace (`id`) | `ProjectsController.cs:35` |
| `POST /api/workspaces/{id}/projects` | Member | Workspace | `ProjectsController.cs:43` |
| `PATCH /api/projects/{id}` | Member | Project | `ProjectsController.cs:57` |
| `DELETE /api/projects/{id}` (archive project) | Admin | Project | `ProjectsController.cs:66` |
| `GET /api/projects/{projectId}/columns` | Viewer | Project | `ColumnsController.cs:27` |
| `POST /api/projects/{projectId}/columns` | Member | Project | `ColumnsController.cs:37` |
| `PATCH /api/columns/{columnId}` | Member | Column | `ColumnsController.cs:51` |
| `DELETE /api/columns/{columnId}` | Admin | Column | `ColumnsController.cs:68` |
| `GET /api/tasks/{taskId}/comments` | Viewer | Task | `TaskCommentsController.cs:28` |
| `POST /api/tasks/{taskId}/comments` | Member | Task | `TaskCommentsController.cs:38` |
| `PATCH /api/comments/{commentId}` | Member | Comment | `TaskCommentsController.cs:52` |
| `DELETE /api/comments/{commentId}` | Member | Comment | `TaskCommentsController.cs:66` |
| `GET /api/tasks/{taskId}/attachments` | Viewer | Task | `TaskAttachmentsController.cs:29` |
| `POST /api/tasks/{taskId}/attachments` | Member | Task | `TaskAttachmentsController.cs:47` |
| `GET /api/attachments/{attachmentId}/download` | Viewer | Attachment | `TaskAttachmentsController.cs:67` |
| `DELETE /api/attachments/{attachmentId}` | Member | Attachment | `TaskAttachmentsController.cs:78` |
| `GET /api/tasks/{taskId}/activity` | Viewer | Task | `TaskActivityController.cs:23` |
| `GET /api/workspaces/{id}/reports/members` | Viewer | Workspace | `ReportsController.cs:27` |
| `GET /api/workspaces/{id}/members` | Viewer | Workspace | `WorkspacesController.cs:56` |
| `POST /api/workspaces/{id}/invitations` | Admin | Workspace | `WorkspacesController.cs:64` |
| `PATCH /api/workspaces/{id}/members/{userId}` | Admin | Workspace | `WorkspacesController.cs:73` |
| `DELETE /api/workspaces/{id}/members/{userId}` | Admin | Workspace | `WorkspacesController.cs:82` |

No attribute on: `GET /api/workspaces`, `POST /api/workspaces` (`WorkspacesController.cs:39-47`), `POST /api/invitations/accept` (`InvitationsController.cs:29`), `AuthController` (`[AllowAnonymous]`, `AuthController.cs:13`).

### 1.2 Inline role checks

Membership writes go through `WorkspaceMembershipPolicy` after the Admin filter:

- `AdminMayManagePeerAdmins = false` (`WorkspaceMembershipPolicy.cs:20`). An Admin cannot change or remove another Admin.
- `AdminMayGrantAdmin = false` (`:29`). An Admin cannot grant Admin.
- `EnsureCanInvite` (`:37-38`) treats the invitee as a Viewer and calls `EnsureCanManage`.
- `EnsureCanManage` (`:44-79`): Owner returns immediately (`:46-47`). Anyone else who is not Admin → 403 `member_manage_forbidden` (`:49-50`). Target or new role Owner → 403 `owner_role_owner_only` (`:52-53`). Admin removing self → 403 `admin_remove_owner_only` (`:55-58`). Admin raising own role → 403 `cannot_raise_own_role` (`:61-62`); lowering own role is allowed (`:64`). Peer Admin → 403 `admin_manage_peer_owner_only` (`:67-70`). Target not Viewer or Member → 403 `admin_manages_viewer_member_only` (`:75-76`). New role Admin → 403 `admin_grant_owner_only` (`:78-79`).
- The last-Owner check is **not** in the policy (`:42`). It is `EnsureNotLastOwnerAsync` → 409 `last_owner` (`WorkspaceService.cs:470-479`), and only after `EnsureCanManage` (`:297-306`, `:329-336`). An Admin who targets the Owner is 403, not 409.

Call sites:

- Invite: `WorkspaceService.cs:147-148`. An existing account is added at once at `request.Role` (`:157-164`). A new email gets an `Invitation` with that role.
- Accept: inviter role re-read (`:246-255`). Inviter gone → 403 `forbidden` (single-string `ForbiddenAccessException`, code `forbidden`). Inviter still present → `EnsureCanInvite` again (`:255`). The new member's role is `invitation.Role` (`:269`).
- Role change: `UpdateMemberRoleAsync` (`:285-314`).
- Remove: `RemoveMemberAsync` (`:317-345`). Same policy with `newRole: null`.
- Invite body cannot be Owner: `InviteMemberRequestValidator.cs:12-13` (400 `validation_failed`, after the role filter, before the policy). Role-change body may be Owner; the policy decides (`UpdateMemberRoleRequestValidator.cs:10-12`).

Comment and attachment, after the Member filter:

- Edit comment: author only. Non-author member → 403 `forbidden`. Non-member re-check → 404 `not_found` (`TaskCommentService.cs:102-105`, `:198-202`). Admin cannot edit another person's comment.
- Delete comment: author, or Admin, or Owner (`:205-210`). Otherwise non-member → 404, member → 403 `forbidden` (`:146-149`).
- Delete attachment: same Admin/Owner/author rule (`TaskAttachmentService.cs:334-339`). Failure is 403 `forbidden` only (`:218-219`). This method does not repeat the non-member 404. The filter already returned 404 for a non-member.

Other:

- `NotificationsHub.JoinProject` cannot use the attribute. It allows any non-null workspace role (`NotificationsHub.cs:108-120`). It does not check Viewer vs Member. An archived project is rejected (`:112-113` via `ProjectService.IsArchivedAsync`, `:119-126`). A missing project fails closed (no workspace id).
- Assignee check is membership, not role: 400 `assignee_not_member` (`BoardService.cs:627-639`).
- Signup creates the caller as Owner (`AuthService.cs:106-112`). `POST /api/workspaces` does the same (`WorkspaceService.cs:102-108`). Owning more than the cap is 409 `workspace_limit_reached` (`:80-89`). That cap counts Owner rows only.

### 1.3 What is not in the code

- No restore route, no `TaskRestored`, no purge route, no hard delete of a card. `DELETE /api/tasks/{taskId}` archives (`TasksController.cs:134-143`, `BoardService.Parents.cs:151`).
- No unarchive of a project. `DELETE /api/projects/{id}` sets `IsArchived = true` (`ProjectService.cs:98-107`).
- No workspace delete.
- No project role. `ProjectMember` is `Id`, `ProjectId`, `UserId` only (`src/Worklane.Domain/Entities/ProjectMember.cs:3-10`). Nothing under `src/` inserts a `ProjectMember`. `project_member_exists` is only the unique-index map (`UniqueConstraintConflicts.cs:26-27`).
- ASP.NET Identity roles are registered (`DependencyInjection.cs:48`, table `AspNetRoles`). No application code calls `AddToRole` or `IsInRole`. `role_name_taken` is that table's unique index, not `WorkspaceRole`.
- No leave route. Self-remove is the Admin remove endpoint, and only if the policy allows it.
- No last-Admin rule. The guard is last **Owner** only.

---

## 2. Where roles apply

Roles are per workspace (the tenant). One membership row per user per workspace (`WorkspaceMember.cs:6-11`). That role applies to every project in the workspace. The hub, board, and task routes all resolve the workspace and then read `WorkspaceMembers`.

Project-level roles do not exist. **Proposed:** do not add them for WL-RESTORE. A Viewer of the workspace is a Viewer of every project in it.

---

## 3. Matrix

Cells: **Enforced** (cite) / **Gap** / **Proposed**.

`Admin` and `Owner` are included wherever the minimum is Viewer or Member.

### 3.1 Cards

| Action | Who | Cell |
|---|---|---|
| Create | Member+ | **Enforced** `TasksController.cs:41` |
| Edit (fields, move) | Member+ | **Enforced** `TasksController.cs:64`, `:81` |
| Read (card, list, children, comments, files, activity) | Viewer+ | **Enforced** `TasksController.cs:27`, `:55`, `:127`; comments `:28`; attachments `:29`; activity `TaskActivityController.cs:23` |
| Archive (`DELETE /api/tasks/{taskId}`) | Member+ | **Enforced** `TasksController.cs:139` |
| Permanent delete / purge | — | **Gap.** No route. WL-PARENT A.7 names a future Admin purge (`docs/specs/WL-PARENT-parent-subtasks.md:131`). **Proposed:** leave purge on WL-58 as Admin+. Do not add it in WL-RESTORE. |
| Restore one card | — | **Gap.** No route. **Proposed:** Member+, same people who may archive. |
| List archived cards | — | **Gap.** Archived rows are hidden by the task filter (`WorklaneDbContext.cs:69-72`). **Proposed:** Viewer+ may list; restore stays Member+. |

### 3.2 Parent and child

Archive does not clear `ParentTaskId` (`BoardService.Parents.cs:582-585`).

| Case | Who | Cell |
|---|---|---|
| Set parent | Member+ | **Enforced** `TasksController.cs:104` |
| Clear parent | Member+ | **Enforced** `TasksController.cs:118` |
| Link when parent or child is archived | Member+ reaches the rule | **Enforced** 409 `card_archived` (`BoardService.Parents.cs:300-301`). Viewer is 403 before that. |
| Archive a child alone | Member+ | **Enforced** same archive route. Link kept (A.7, WL-PARENT `:128`). |
| Archive a parent that has active children, without `includeChildren=true` | Member+ | **Enforced** 409 `parent_has_active_children` (`BoardService.Parents.cs:166-171`) |
| Archive a parent with `includeChildren=true` | Member+ | **Enforced** parent and active children archived in one transaction (`:174-177`). Already-archived children are not in that list (`:163-165`). No second role check. |
| Restore a parent that has archived children | — | **Gap.** **Proposed:** Member+ restores the parent only. Children stay archived. WL-PARENT A.7: "Restoring a parent does not restore children" (`WL-PARENT-parent-subtasks.md:130`). That file has no section P-9; this sentence is the rule on `develop`. |
| Restore a child whose parent is still archived | — | **Gap.** **Proposed:** Member+ gets 409 `parent_archived` (catalog row is `planned (WL-PARENT)`, `docs/api-error-codes.md:70`). Viewer gets 403 `workspace_access_denied` and never the 409. |

### 3.3 Invites and members

The HTTP filter is Admin+ (`WorkspacesController.cs:64`, `:73`, `:82`). Member and Viewer get 403 `workspace_access_denied` and never reach the policy. There is no last-Admin rule.

| Action | Who | Cell |
|---|---|---|
| Invite as Viewer or Member | Admin or Owner | **Enforced** filter + `EnsureCanInvite` (`WorkspaceService.cs:148`, policy `:37-38`) |
| Invite as Admin | Owner | **Enforced.** Admin → 403 `admin_grant_owner_only` (`WorkspaceMembershipPolicy.cs:78-79`) |
| Invite as Owner | Nobody | **Enforced** 400 from `InviteMemberRequestValidator.cs:12-13` |
| Change a Viewer or Member among Viewer and Member | Admin or Owner | **Enforced** `UpdateMemberRoleAsync` (`WorkspaceService.cs:285-314`) |
| Promote someone to Admin | Owner | **Enforced** `AdminMayGrantAdmin = false` |
| Grant, change, or remove Owner | Owner, and not the last Owner | **Enforced** `owner_role_owner_only` (`:52-53`); sole Owner → 409 `last_owner` (`WorkspaceService.cs:304-306`, `:333-336`, `:470-479`) |
| Admin changes or removes a different Admin | Owner only | **Enforced** `AdminMayManagePeerAdmins = false` (`:67-70`) |
| Admin removes self | Nobody via this call | **Enforced** `admin_remove_owner_only` (`:55-58`) |
| Admin lowers own role | That Admin | **Enforced** (`:60-64`) |
| Remove another Viewer or Member | Admin or Owner | **Enforced** `RemoveMemberAsync` |
| Leave (Member or Viewer removes self) | — | **Gap.** They fail the Admin filter. **Proposed:** not part of WL-RESTORE. See question 6. |
| Leave (Owner) | That Owner, if another Owner remains | **Enforced** (policy allows Owner; `last_owner` if sole) |
| Last Admin must remain | — | **Does not exist.** **Proposed:** do not add it. Last Owner stays the rule. |

Accept still checks the inviter at accept time (`WorkspaceService.cs:246-255`). A demoted inviter can make a previously valid invite fail with the policy's 403.

---

## 4. Error codes and order

Catalog: `docs/api-error-codes.md`. Order: `docs/conventions-api.md` §Errors (`:196-206`) and WL-PARENT A.5 (`WL-PARENT-parent-subtasks.md:91`).

For a workspace route, first match wins:

1. 401 — missing or invalid JWT (empty body), or empty user id inside the filter (`unauthorized`).
2. 429 `rate_limited` — only where a limiter is enabled. Restore has none today.
3. 413 `request_too_large` — body limit, before the action.
4. 400 `invalid_route_id` or `invalid_request_body`.
5. 404 `not_found` — id does not resolve, **or** the caller is not a member. Same body: detail `Resource was not found.` (`RequireWorkspaceRoleAttribute.cs:97-100`, `:114-115`). This is before any 403.
6. 403 `workspace_access_denied` — member below the minimum (`:103-106`).
7. 400 `validation_failed` — validator inside the action.
8. 409 — domain rules (`parent_archived`, `parent_child_limit`, and the existing parent codes).

`forbidden` is the later, finer denial: comment edit/delete and attachment delete (`docs/api-error-codes.md:15`, `:30`). It is not the code for "role too low."

**Proposed for WL-RESTORE R1 and R2:** use that order. A Viewer who may not restore gets **403 `workspace_access_denied`**, not `forbidden`. WL-RESTORE §3 sketches 403 `forbidden`; this plan replaces that sketch so restore matches archive. No new error code. `parent_archived` stays the planned 409 (`docs/api-error-codes.md:70`) and is evaluated only after 404 and 403.

A purged or missing id does not resolve, so every caller, including a Member, gets 404 from step 5. A Viewer does not get 403 for an id that is already gone. An archived card still resolves, so a Viewer gets 403 and a Member reaches the 409 rules.

---

## 5. WL-RESTORE

WL-RESTORE §7 says the role plan does not exist and blocks AC 5 (the 403) and the rule that hides Restore. This doc is that plan. It does not lift the hold.

| WL-RESTORE question | Answer in this plan |
|---|---|
| **Q-1** Who may restore? | **Proposed:** Member, Admin, and Owner. Same minimum as archive (`TasksController.cs:139`). Viewer: 403 `workspace_access_denied`. Not "only the person who archived it." Not Admin-only. Blocking until อรุษ confirms question 1 below. |
| **Q-2** Auto-restore children when the parent is restored? | Not a role rule. **Follow WL-PARENT A.7** (`:130`): restore the parent only; children stay archived. Matches WL-RESTORE AC 7. No section named P-9 exists in `docs/specs/WL-PARENT-parent-subtasks.md` on `develop`. |
| **Q-3** Original column was deleted. | Not a role rule. No restore code exists, so nothing enforces a choice. WL-RESTORE's PO lean (first column, plus a toast) stays with SA. Role checks still run before that 409. |
| **Q-4** May a Viewer open the archived list? | **Proposed:** yes, read-only (Viewer+), same as other card reads. Restore is omitted for Viewer (403 if they call it). |
| **Q-5** "Will be purged on" hint. | Not a role rule. Out of this doc and out of WL-RESTORE, as that spec already says. |

If Q-1 and Q-4 are accepted, WL-RESTORE can write AC 5 as: non-member → 404 `not_found` (same body as a random id); Viewer → 403 `workspace_access_denied`; Member+ → the restore behaviour. The button is shown only for Member+.

No new role, no project role, and no schema change are required for that.

### Suggested tickets

1. **This doc** — owner review only.
2. **WL-RESTORE** — after the hold lifts, implement R1 (Viewer+) and R2 (Member+) with the order in §4. Absorbs WL-PARENT BE-4b (`parent_archived` + cap). Do not auto-restore children.
3. **WL-58 purge** — still separate. Proposed minimum Admin+, already stated in WL-PARENT A.7. Not required to restore.
4. **Leave for Member/Viewer** — only if question 6 is yes. Not a WL-RESTORE blocker.

---

## 6. Open questions for อรุษ

1. **Confirm Q-1:** restore is Member+ (anyone who may archive), and the 403 code is `workspace_access_denied`.
2. **Confirm Q-4:** Viewer may list archived cards and may not restore.
3. **Confirm Q-2 stays as WL-PARENT A.7:** restoring a parent does not restore its children. Say if children should be offered as a second step instead.
4. **Q-3** is outside this plan. Confirm the PO lean (first column + toast) or send it back to SA. It does not change who may restore.
5. **Q-5** stays out. No retention hint in the role plan.
6. Should a Member or Viewer be able to leave a workspace themselves? Today they cannot.
7. Keep last-Owner as the only "last seat" rule, with no last-Admin rule?
8. Leave `AdminMayGrantAdmin` and `AdminMayManagePeerAdmins` both `false`?
9. Confirm project-level roles stay out of scope.
