# API error codes

Errors are RFC 7807 problem documents. Each body has a stable snake_case `code`. Counts and sizes are camelCase numbers. `detail` is English. The Web app localizes from `code`; the API does not.

The JWT bearer challenge 401 is the only error with an empty body. The Web app uses the status.

Reserved names are not overwritten by exception parameters, compared without regard to case: `type`, `title`, `status`, `detail`, `instance`, `code`, `errors`, `fieldCodes`, `traceId`.

`unsupported_media_type`, `not_found`, and `unauthorized` are generic for now. Later cards rename them to `unsupported_file_type`, `<entity>_not_found`, and `invalid_credentials` / `refresh_token_invalid` (WL-64).

| code | status | extensions |
| --- | --- | --- |
| `bad_request` | 400 | |
| `file_empty` | 400 | |
| `invalid_request_body` | 400 | |
| `invalid_route_id` | 400 | |
| `validation_failed` | 400 | `errors`, `fieldCodes` |
| `unauthorized` | 401 | |
| `forbidden` | 403 | |
| `workspace_access_denied` | 403 | |
| `not_found` | 404 | |
| `conflict` | 409 | |
| `quota_exceeded` | 409 | `usedBytes`, `quotaBytes`, `fileBytes` when the size is known |
| `request_too_large` | 413 | |
| `file_too_large` | 413 | `maxBytes` |
| `unsupported_media_type` | 415 | |
| `rate_limited` | 429 | |
| `internal_error` | 500 | `traceId` |

`file_empty` is a 0-byte upload. `request_too_large` is a body over the server limit, rejected before the body is read. `file_too_large` is one attachment over the per-file limit. `fieldCodes` values are per-field snake_case codes, not response codes.
