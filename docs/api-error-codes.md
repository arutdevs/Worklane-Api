# API error codes

This is the catalog of API error codes. The Web app localizes from `code`. The API does not.

Errors are RFC 7807 problem documents. Each body has a stable snake_case `code`. Counts and sizes are camelCase numbers. `detail` is English.

The JWT bearer challenge 401 is the only error with an empty body. The Web app uses the status.

Reserved names are not overwritten by exception parameters, compared without regard to case: `type`, `title`, `status`, `detail`, `instance`, `code`, `errors`, `fieldCodes`, `traceId`.

`not_found` and `unauthorized` are generic for now. Later cards rename them to `<entity>_not_found`, and to `invalid_credentials` / `refresh_token_invalid` (WL-64).

`RequireWorkspaceRoleAttribute`: a non-member gets 404 `not_found` with the same body as a non-existent id. 403 `workspace_access_denied` is only for a member whose role is too low. That filter change ships in WL-88. Until then, a non-member still gets 403 `workspace_access_denied`.

`Status` is `emitted` when a current branch sends that code, or `planned (card)` when it is not sent yet. WL-60 codes are emitted by the column-delete PR stacked on this one.

| code | HTTP | extensions | detail | Status |
| --- | --- | --- | --- | --- |
| `bad_request` | 400 | | The request is invalid. | emitted |
| `file_empty` | 400 | | A non-empty file is required. | emitted |
| `invalid_request_body` | 400 | | The request is invalid. | emitted |
| `invalid_route_id` | 400 | | Route parameter "{name}" is missing or not a valid id. | emitted |
| `assignee_not_member` | 400 | | Assignee must be a member of this workspace. | emitted |
| `validation_failed` | 400 | `errors`, `fieldCodes` | The validator's English message. `fieldCodes` are per-field codes, not response codes. `not_empty` is emitted for `NotEmptyValidator`. `not_nullable` is emitted when `Title`, `Priority`, or `ColumnId` is JSON null. | emitted |
| `target_column_same_as_source` | 400 | | Target column must be different from the column being deleted. | emitted |
| `unauthorized` | 401 | | A valid access token is required. | emitted |
| `forbidden` | 403 | | You do not have permission to perform this action. | emitted |
| `workspace_access_denied` | 403 | | You do not have sufficient permissions in this workspace. A non-member is 404 `not_found`, same body as a missing id, once WL-88 lands. Until then a non-member still gets this 403. | emitted |
| `file_infected` | 403 | | The file was rejected by malware scanning. | planned (WL-73) |
| `member_manage_forbidden` | 403 | | You do not have permission to manage workspace members. | planned (WL-62) |
| `owner_role_owner_only` | 403 | | Only an Owner can grant, change, or remove the Owner role. | planned (WL-62) |
| `admin_remove_owner_only` | 403 | | Only an Owner can remove an Admin. | planned (WL-62) |
| `cannot_raise_own_role` | 403 | | You cannot raise your own role. | planned (WL-62) |
| `admin_manage_peer_owner_only` | 403 | | Only an Owner can change or remove another Admin. | planned (WL-62) |
| `admin_manages_viewer_member_only` | 403 | | An Admin can only manage Viewers and Members. | planned (WL-62) |
| `admin_grant_owner_only` | 403 | | Only an Owner can grant the Admin role. | planned (WL-62) |
| `not_found` | 404 | | Resource was not found. | emitted |
| `target_column_not_found` | 404 | | Target column was not found in this project. | emitted |
| `conflict` | 409 | | The request conflicts with the current state. | emitted |
| `registration_failed` | 409 | | Identity's error text, joined with spaces and not translated. A later change to 400 `validation_failed` with `fieldCodes` is waiting on Tech Lead and frontend confirmation, and on a separate PO card. Until `registration_failed` is emitted, this path returns `conflict` with that text. | planned |
| `last_owner` | 409 | | Cannot remove or demote the last Owner of a workspace. Until `last_owner` is emitted, this path returns `conflict` with that sentence. | planned |
| `quota_exceeded` | 409 | `usedBytes`, `quotaBytes`, `fileBytes` when the size is known. `plan` is planned. | This workspace has used {usedBytes} of {quotaBytes} bytes. | emitted |
| `column_has_active_cards` | 409 | `activeTaskCount`, `archivedTaskCount` | Column has {active} card(s) and {archived} archived card(s). Provide targetColumnId to move them first. | emitted |
| `column_has_archived_cards` | 409 | `activeTaskCount`, `archivedTaskCount` | Column has {archived} archived card(s). Choose a column to move them to. | emitted |
| `last_column_has_archived_cards` | 409 | `archivedTaskCount`, and `activeTaskCount` when the column also has active cards | This is the project's last column and it has {archived} archived card(s). Purge them or add another column first. | emitted |
| `column_delete_conflict` | 409 | | The column changed while it was being deleted. Reload the board and try again. | emitted |
| `request_too_large` | 413 | | The request body exceeds the size limit. | emitted |
| `file_too_large` | 413 | `maxBytes` | File exceeds the maximum size of {maxBytes} bytes. | emitted |
| `unsupported_file_type` | 415 | `extension` | The file type is not allowed. | emitted |
| `rate_limited` | 429 | `scope` | Too many requests. | emitted |
| `internal_error` | 500 | `traceId` | An unexpected error occurred. | emitted |

`file_empty` is a 0-byte upload. `request_too_large` is a body over the server limit, rejected before the body is read, and has no `maxBytes`. `file_too_large` is one attachment over the per-file limit. `unsupported_file_type` is every rejected upload type, including the generic 415 fallback. `extension` is the filename extension the caller sent, including the leading dot. `rate_limited` carries `scope` and no usage or limit numbers. The wait is only the `Retry-After` header, which CORS exposes. Emitted `scope` is `user` (global limiter, layer 1). Planned scopes use the same code and fields: `workspace_attachments` (WL-88 layer 2), `workspace_ai` (AI quota), `ip` (WL-64 login per IP). WL-62 sentences are the ones on `cursor/wl-62-member-role-guard-4be8`; that branch still sends them as `forbidden`.
