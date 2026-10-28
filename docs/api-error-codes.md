# API error codes

This is the catalog of API error codes. The Web app localizes from `code`. The API does not.

Errors are RFC 7807 problem documents. Each body has a stable snake_case `code`. Counts and sizes are camelCase numbers. `detail` is English.

The JWT bearer challenge 401 is the only error with an empty body. The Web app uses the status.

Reserved names are not overwritten by exception parameters, compared without regard to case: `type`, `title`, `status`, `detail`, `instance`, `code`, `errors`, `fieldCodes`, `traceId`.

`fieldCodes` keys are currently the PascalCase property names. That is existing behaviour; this note does not change it.

`not_found` and `unauthorized` are generic for now. Later cards rename them to `<entity>_not_found`, and to `invalid_credentials` / `refresh_token_invalid` (WL-64).

`RequireWorkspaceRoleAttribute`: a non-member gets 404 `not_found` with the same body as a non-existent id. 403 `workspace_access_denied` is only for a member whose role is too low. Comment edit and delete use that same 404 when the caller is not a member. A member who is not allowed to edit or delete still gets 403 `forbidden`.

`Status` is `emitted` when a current branch sends that code, or `planned (card)` when it is not sent yet. WL-60 codes are emitted by the column-delete PR stacked on this one.

| code | HTTP | extensions | detail | Status |
| --- | --- | --- | --- | --- |
| `bad_request` | 400 | | The request is invalid. | emitted |
| `file_empty` | 400 | | A non-empty file is required. | emitted |
| `invalid_request_body` | 400 | | The request is invalid. | emitted |
| `invalid_route_id` | 400 | | Route parameter "{name}" is missing or not a valid id. | emitted |
| `assignee_not_member` | 400 | | Assignee must be a member of this workspace. | emitted |
| `validation_failed` | 400 | `errors`, `fieldCodes` | The validator's English message. `fieldCodes` are per-field codes, not response codes. `not_empty` is emitted for `NotEmptyValidator` and for an empty or whitespace card title or workspace name. `not_nullable` is emitted when `Title`, `Priority`, or `ColumnId` is JSON null. | emitted |
| `target_column_same_as_source` | 400 | | Target column must be different from the column being deleted. | emitted |
| `unauthorized` | 401 | | A valid access token is required. | emitted |
| `forbidden` | 403 | | You do not have permission to perform this action. | emitted |
| `workspace_access_denied` | 403 | | You do not have sufficient permissions in this workspace. A non-member is 404 `not_found`, the same body as a missing id. | emitted |
| `file_infected` | 403 | | The file was rejected by malware scanning. | planned (WL-73) |
| `member_manage_forbidden` | 403 | | You do not have permission to manage workspace members. | emitted |
| `owner_role_owner_only` | 403 | | Only an Owner can grant, change, or remove the Owner role. | emitted |
| `admin_remove_owner_only` | 403 | | Only an Owner can remove an Admin. | emitted |
| `cannot_raise_own_role` | 403 | | You cannot raise your own role. | emitted |
| `admin_manage_peer_owner_only` | 403 | | Only an Owner can change or remove another Admin. | emitted |
| `admin_manages_viewer_member_only` | 403 | | An Admin can only manage Viewers and Members. | emitted |
| `admin_grant_owner_only` | 403 | | Only an Owner can grant the Admin role. | emitted |
| `not_found` | 404 | | Resource was not found. | emitted |
| `target_column_not_found` | 404 | | Target column was not found in this project. | emitted |
| `conflict` | 409 | | The request conflicts with the current state. | emitted |
| `workspace_slug_unavailable` | 409 | | A unique workspace address could not be reserved. Try again. | emitted |
| `workspace_limit_reached` | 409 | `limit` | You can own at most {limit} workspaces. | emitted |
| `email_already_registered` | 409 | | An account with this email already exists. | emitted |
| `project_key_taken` | 409 | | A project with this key already exists in this workspace. | emitted |
| `workspace_member_exists` | 409 | | This user is already a member of the workspace. | emitted |
| `invitation_token_taken` | 409 | | An invitation could not be created. Try again. | emitted |
| `project_member_exists` | 409 | | This user is already a member of the project. | emitted |
| `refresh_token_conflict` | 409 | | The session could not be saved. Try again. | emitted |
| `attachment_storage_key_taken` | 409 | | The file could not be stored. Try again. | emitted |
| `task_number_taken` | 409 | | The card number was already used. Try again. | emitted |
| `role_name_taken` | 409 | | A role with this name already exists. | emitted |
| `unique_conflict` | 409 | | The request conflicts with an existing record. | emitted |
| `registration_failed` | 409 | | Identity's error text, joined with spaces and not translated. A later change to 400 `validation_failed` with `fieldCodes` is waiting on Tech Lead and frontend confirmation, and on a separate PO card. Until `registration_failed` is emitted, password and other Identity failures return `conflict` with that text. A duplicate email is `email_already_registered`, not this code. | planned |
| `last_owner` | 409 | | Cannot remove or demote the last Owner of a workspace. | emitted |
| `quota_exceeded` | 409 | `usedBytes`, `quotaBytes`, `fileBytes` when the size is known. `plan` is planned. | This workspace has used {usedBytes} of {quotaBytes} bytes. | emitted |
| `column_has_active_cards` | 409 | `activeTaskCount`, `archivedTaskCount` | Column has {active} card(s) and {archived} archived card(s). Provide targetColumnId to move them first. | emitted |
| `column_has_archived_cards` | 409 | `activeTaskCount`, `archivedTaskCount` | Column has {archived} archived card(s). Choose a column to move them to. | emitted |
| `last_column_has_archived_cards` | 409 | `archivedTaskCount`, and `activeTaskCount` when the column also has active cards | This is the project's last column and it has {archived} archived card(s). Purge them or add another column first. | emitted |
| `column_delete_conflict` | 409 | | The column changed while it was being deleted. Reload the board and try again. | emitted |
| `request_too_large` | 413 | | The request body exceeds the size limit. | emitted |
| `file_too_large` | 413 | `maxBytes` | File exceeds the maximum size of {maxBytes} bytes. | emitted |
| `unsupported_file_type` | 415 | `extension` | The file type is not allowed. | emitted |
| `rate_limited` | 429 | `scope` (`user` on `POST /api/workspaces`) | Too many requests. | emitted |
| `internal_error` | 500 | `traceId` | An unexpected error occurred. | emitted |

`workspace_slug_unavailable` is emitted when signup or `POST /api/workspaces` hits a unique violation on `IX_Workspaces_Slug` and the bounded suffix retries are exhausted. Signup rolls the user, membership, and workspace back together. Any other failure during that transaction rolls back the same way and does not emit this code. The same code is the backstop when an `IX_Workspaces_Slug` violation is not caught by that retry.

`workspace_limit_reached` is emitted by `POST /api/workspaces` when the caller already owns `WorkspaceCreation:MaxOwnedWorkspaces` workspaces (default 20). `limit` is that number. The count is membership rows with role Owner, so a Member or Viewer seat in another workspace does not count. Signup does not use this check: registration still creates the personal workspace, and that workspace counts toward the cap afterwards.

`email_already_registered` is a duplicate email on register, whether the row was already there or two requests raced. Signup keeps one transaction. The loser rolls back, so there is no second user and no workspace without that user. `EmailIndex` and `UserNameIndex` both map here because the account name is the email.

A Postgres unique violation that is not caught in a handler becomes **409** from one map, not 500. `IX_Projects_WorkspaceId_Key` is `project_key_taken`. `IX_WorkspaceMembers_WorkspaceId_UserId` is `workspace_member_exists`. `IX_Invitations_Token` is `invitation_token_taken`. `IX_ProjectMembers_ProjectId_UserId` is `project_member_exists`. `IX_RefreshTokens_TokenHash` is `refresh_token_conflict`. `IX_TaskAttachments_StorageKey` is `attachment_storage_key_taken`. `IX_TaskItems_ProjectId_Number` is `task_number_taken`. `RoleNameIndex` is `role_name_taken`. Any other unique violation, including a primary key, is `unique_conflict`. The body does not include the constraint name.

These indexes were audited and are not unique, so they have no 409 code: `IX_BoardColumns_ProjectId_Order`, `IX_TaskItems_ColumnId_Order`, `IX_TaskComments_TaskId_CreatedAt`, `IX_TaskActivities_TaskId_CreatedAt`, `IX_TaskAttachments_TaskId_CreatedAt`, `IX_WorkspaceMembershipEvents_WorkspaceId_OccurredAt`, `IX_Invitations_WorkspaceId`, and `IX_RefreshTokens_UserId`. Duplicate column names or orders are not a unique violation.

`file_empty` is a 0-byte upload. `request_too_large` is a body over the server limit, rejected before the body is read, and has no `maxBytes`. `file_too_large` is one attachment over the per-file limit. `unsupported_file_type` is every rejected upload type, including the generic 415 fallback. `extension` is the filename extension the caller sent, including the leading dot. `rate_limited` carries `scope` and no usage or limit numbers. The wait is only the `Retry-After` header, which CORS exposes. The body has no `retryAfterSeconds`. Emitted `scope` is `user` (global limiter, layer 1, including comments, attachments, and `POST /api/workspaces`) or `ip` (login, register, and refresh, one bucket per resolved client IP, `RateLimit:Auth`). `POST /api/workspaces` uses policy `workspace-create` (`WorkspaceCreation:PermitLimit` and `WorkspaceCreation:WindowSeconds`, default 10 permits per 3600 seconds), one bucket per JWT `sub`. That bucket does not read `X-Forwarded-For` and does not follow the client address, including an address adopted from a trusted proxy. The body still has no limit numbers and no `retryAfterSeconds`. On that endpoint the order is 401, then 429, then 400, then 409. The resolved address is a single `CF-Connecting-IP` when the TCP peer is a configured proxy, otherwise the rightmost `X-Forwarded-For` value from that proxy (`ForwardLimit` is 1, so further hops to the left are ignored), otherwise the socket address. Deploy trusts only the edge proxy `172.30.20.10` (`ForwardedHeaders:KnownProxies`) and does not set `KnownNetworks`, so the rest of `172.30.20.0/24` is not a proxy. The Web nginx is pinned at `172.30.20.3` and is not a second trusted proxy; it forwards the client to that edge. A spoofed header from any other peer, including `172.30.20.3` when it connects to the API directly, is ignored. Planned scopes use the same code and fields: `workspace_attachments` (WL-88 layer 2), `workspace_ai` (AI quota). WL-62 emits `member_manage_forbidden`, `owner_role_owner_only`, `admin_remove_owner_only`, `cannot_raise_own_role`, `admin_manage_peer_owner_only`, `admin_manages_viewer_member_only`, `admin_grant_owner_only`, and `last_owner`.
