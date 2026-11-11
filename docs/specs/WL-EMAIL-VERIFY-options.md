# WL-EMAIL-VERIFY — Signup email verification (options)

- **Status:** Options for the owner. Nothing here is chosen. The suggestion at the end is the SA's recommendation and stays pending that choice.
- **This PR:** Design note only. No code, migration, or test changes. Do not run any migration.
- **Code cited:** `develop` @ `e99d3fb235d5300a1c1a30e2c54f4eba6e2b6f5d`. Line numbers are from that commit. Anything that does not exist in that tree is marked **new**.
- **Sources:** security review of that commit (invite and auth, 2026-11-11); `AGENTS.md`; `docs/conventions-api.md`; `docs/api-error-codes.md`.
- **Out of this note:** the H1 invite-token fix and the M1 lockout fix. Both are in progress with another developer. They are dependencies only.

The Web app is the `Worklane-Web` repository (`AGENTS.md`). This checkout has no `web/` tree. Web notes below are the contract the API would expose.

## 1. Dependencies already in progress

**H1 (do not redesign).** `AuthService.RegisterNewUserAsync` creates the account and issues tokens with `EmailConfirmed` left at the Identity default `false` (`src/Worklane.Infrastructure/Services/AuthService.cs:71-77`, `:116`). `WorkspaceService.InviteMemberAsync` then, when that email already has a user, inserts `WorkspaceMembers` immediately and returns `InvitationDto` with `Id = Guid.Empty` (`src/Worklane.Infrastructure/Services/WorkspaceService.cs:150-171`). The in-progress fix is: auto-add only when `existingUser.EmailConfirmed`; otherwise create the normal token invitation (`:174-194`). This note assumes that fix lands. It does not specify the patch.

**M1 (do not redesign).** Login calls `UserManager.CheckPasswordAsync` and throws `AuthenticationFailedException` (`unauthorized`, detail `Invalid credentials.`) for a missing user and a wrong password (`AuthService.cs:122-126`; `src/Worklane.Application/Common/Exceptions/AuthenticationFailedException.cs:9-10`). Lockout options are not set (`src/Worklane.Infrastructure/DependencyInjection.cs:43-47`). The in-progress fix counts failures, locks the account, and keeps that same 401 body while locked. Any verification response on login has to stay compatible with that uniform 401. This note does not specify lockout thresholds.

Related, also not designed here: invite create has no rate limit (`src/Worklane.WebApi/Controllers/WorkspacesController.cs:63-64`), so an Admin can already relay mail through the Worklane sender (review L3). Verification limits below do not close that.

## 2. What the code does today

### Identity, tokens, Data Protection

`AddIdentityCore<AppUser>` sets `Password.RequiredLength = 8` and `User.RequireUniqueEmail = true`, then `AddRoles`, `AddEntityFrameworkStores`, and `AddDefaultTokenProviders` (`DependencyInjection.cs:43-50`).

Not present in `src`: `SignIn.RequireConfirmedEmail`, `SignInManager`, `DataProtectionTokenProviderOptions`, `TokenLifespan`, and any call to `GenerateEmailConfirmationTokenAsync` / `ConfirmEmailAsync`. Login does not read `EmailConfirmed`, so setting `RequireConfirmedEmail` alone would not change `LoginAsync`.

`AddDefaultTokenProviders` registers Identity's Data Protection token provider. The key ring is `AddWorklaneDataProtection`: application name `Worklane`, keys at `DataProtection:KeysPath` (`src/Worklane.WebApi/Startup/DataProtectionRegistration.cs:10-37`; `src/Worklane.WebApi/Program.cs:195`). Outside Development and Testing an empty path refuses to start. `appsettings.json` leaves `DataProtection:KeysPath` empty (`:34-36`). Token lifetime is not configured. Identity's protector default is one day; that default is framework behaviour, not a Worklane setting.

`AppUser` extends `IdentityUser<Guid>` with `DisplayName`, `AvatarUrl`, and `PreferredLanguage` (`src/Worklane.Infrastructure/Identity/AppUser.cs:6-10`). `EmailConfirmed` is the Identity column, not a property declared on `AppUser`.

### Register, login, refresh, JWT

| Action | Route | Auth | Limit | Success |
|---|---|---|---|---|
| Register | `POST /api/auth/register` | `[AllowAnonymous]` | policy `auth` | **201** `AuthTokensResponse` |
| Login | `POST /api/auth/login` | anonymous | policy `auth` | **200** tokens |
| Refresh | `POST /api/auth/refresh` | anonymous | policy `auth` | **200** tokens |
| Logout | `POST /api/auth/logout` | anonymous | none | **204** |

`src/Worklane.WebApi/Controllers/AuthController.cs:11-65`. There is no other auth route (no password reset, no email change, no "me").

Register rejects an existing email with **409** `email_already_registered` (`AuthService.cs:62-64`; code and detail at `src/Worklane.Infrastructure/Persistence/UniqueConstraintConflicts.cs:14-15`). It then creates the user with `UserName = Email`, creates a personal workspace, adds the user as `Owner`, and issues tokens (`AuthService.cs:69-116`). That write runs inside the transaction `WorkspaceSlug.InsertAsync` opens and commits after the callback returns (`src/Worklane.Infrastructure/Services/WorkspaceSlug.cs:59-66`), so the refresh token commits with the user. `EmailConfirmed` is not set. Language is `WorkspaceLocale.Resolve` (`en` or `th` only; `src/Worklane.Infrastructure/Services/WorkspaceLocale.cs:12-14`, `:48-51`).

Login and refresh never read `EmailConfirmed` (`AuthService.cs:120-157`). Refresh rotates the presented token and does not check a stamp.

The access token is HS256, claims `sub`, `email`, `displayName`, `jti` (`src/Worklane.Infrastructure/Identity/JwtTokenService.cs:20-42`). Lifetime is `Jwt:AccessTokenExpiryMinutes` (15) and the refresh value is 64 random bytes, stored as SHA-256 hex, for `Jwt:RefreshTokenExpiryDays` (7) (`JwtTokenService.cs:45-55`; `appsettings.json:12-18`; `AuthService.cs:176-182`). Validation is issuer, audience, signing key, lifetime, clock skew 30 seconds (`src/Worklane.WebApi/Startup/JwtBearerOptionsSetup.cs:28-38`). No `email_verified` claim exists.

`UserDto` is `(Id, Email, DisplayName, AvatarUrl, PreferredLanguage)` (`src/Worklane.Application/Auth/Dtos/AuthDtos.cs:11-13`). No confirmed flag.

Password rules on the wire are the register validator: not empty, length ≥ 8, one upper, one lower, one digit (`src/Worklane.Application/Auth/Validators/RegisterRequestValidator.cs:10-16`). Login checks non-empty email and password (`LoginRequestValidator.cs:10-11`).

### Invite, accept, workspace join

Create invite: `POST /api/workspaces/{id}/invitations`, `[Authorize]`, `[RequireWorkspaceRole(Admin)]` (`WorkspacesController.cs:63-69`). Validator: email, role in enum, role not `Owner` (`src/Worklane.Application/Workspaces/Validators/InviteMemberRequestValidator.cs:10-13`). `EnsureCanInvite` (`src/Worklane.Application/Workspaces/WorkspaceMembershipPolicy.cs:37-38`).

Two paths in `InviteMemberAsync`:

- Existing account: membership row immediately, `InvitationDto` with `Guid.Empty` (`WorkspaceService.cs:150-171`). This is the H1 path.
- No account: invitation row, 32 random bytes, SHA-256 hex in `TokenHash`, 7-day `ExpiresAt`, raw token only in the link (`:174-194`; `src/Worklane.Infrastructure/Services/InviteTokens.cs:6-17`; `src/Worklane.Domain/Entities/Invitation.cs:5-15`).

The link is `Frontend:BaseUrl` plus `/invitations/accept?token=` (`src/Worklane.Infrastructure/Services/InviteLinks.cs:3-12`). `Frontend:BaseUrl` is required outside Development, absolute http(s), and not loopback outside Development (`src/Worklane.Infrastructure/Services/FrontendSettingsValidator.cs:15-32`). The API accept route takes the token in JSON, not the query.

Accept: `POST /api/invitations/accept`, `[Authorize]`, body `{ token }` (`src/Worklane.WebApi/Controllers/InvitationsController.cs:11-35`; validator `AcceptInvitationRequestValidator.cs:10`). No tenant is pinned. Unknown, expired, already accepted, and wrong-account tokens all throw `NotFoundException` with detail `Invitation is invalid or has expired.` (`WorkspaceService.cs:229-235`, `:367-368`), which is code `not_found` (`src/Worklane.Application/Common/Exceptions/NotFoundException.cs:9`). Email match is `OrdinalIgnoreCase` (`:231`). The inviter's current role is checked again (`:246-255`).

Joining a workspace is only that auto-add, that accept, or register's personal Owner row. `POST /api/workspaces` creates another workspace the caller owns (`WorkspacesController.cs:46-52`). There is no separate "join" route.

### Email sender

`IEmailService` has one method, `SendAsync(toEmail, subject, htmlBody, cancellationToken)` (`src/Worklane.Application/Common/Interfaces/IEmailService.cs:3-6`). The only implementation is `SmtpEmailService`, registered at `DependencyInjection.cs:82`. Settings section `Email`: `SmtpHost`, `SmtpPort` (default 587), `SmtpUsername` (also the From address), `SmtpPassword`, `FromName` default `Worklane` (`src/Worklane.Application/Common/Options/EmailSettings.cs:3-15`). `appsettings.json:20-24` sets host `smtp.gmail.com`, port 587, and `FromName`. Username and password are not in that file (user-secrets; `PROGRESS.md`).

`System.Net.Mail.SmtpClient` sends, then logs `Sent email to {ToEmail}: {Subject}` (`SmtpEmailService.cs:21-39`). Invite HTML is an English interpolated string; the workspace name is HTML-encoded; the link is not (`WorkspaceService.cs:206-212`). Send failure is caught, logged as a warning with the address, and the invite still returns (`:197-217`). The log line for creation includes the address and not the token (`:191-194`).

Not present: a Resend (vendor) client, an API key, `IEmailSender` / `IEmailSender<TUser>`, template files, a bounce webhook, and a retry queue.

### Rate limiting (two layers)

**Layer 1, what runs.** `AddRateLimiter` in `Program.cs:89-177`. Rejection is **429** `application/problem+json`, `code` `rate_limited`, `scope`, title `Too Many Requests`, detail `Too many requests.` (`:100-114`). `Retry-After` is the only wait signal (seconds, at least 1) (`:94-98`). The body has no `retryAfterSeconds` and no limit numbers. CORS exposes `Retry-After` (`:192`). `ExceptionHandlingMiddleware` maps 429 to `rate_limited` as well (`src/Worklane.WebApi/Middleware/ExceptionHandlingMiddleware.cs:202`). Catalog row: `docs/api-error-codes.md:75`.

`scope` is `ip` when the endpoint policy name is `auth`, and `user` for every other policy (`Program.cs:172-176`). There is no `GlobalLimiter`.

| Policy | Where | Key | Defaults |
|---|---|---|---|
| `auth` | register, login, refresh | client IP (`ForwardedClientIp.PartitionKey`, `src/Worklane.WebApi/Middleware/ForwardedClientIp.cs:41-48`) | `RateLimit:Auth` 10 per 60s (`src/Worklane.Application/Common/Options/AuthRateLimitSettings.cs:6-15`; `appsettings.json:75-78`) |
| `comments`, `attachments` | those routes | JWT `sub` plus workspace (`AttachmentRateLimitPartition.cs:14-24`) | 60/min and 30/min |
| `workspace-create` | `POST /api/workspaces` | JWT `sub` only (`WorkspaceCreateRateLimitPartition.cs:11-17`) | 10 per 3600s |

One `[EnableRateLimiting]` attribute per action. Each policy is a single fixed window. Logout is not limited. The pipeline is authentication, authorization, then the limiter, then the body-size middleware (`Program.cs:249-252`).

**Layer 2, named and not built.** `docs/api-error-codes.md:90` reserves further `scope` values on the same code: `workspace_attachments` (WL-88) and `workspace_ai`. No second limiter runs in `Program.cs`. Email verification should use layer 1. It should not invent a layer-2 scope.

### `EmailConfirmed` and migrations

Column `AspNetUsers.EmailConfirmed`, `boolean`, not null, added with the table in `20260714120745_InitialCreate` (`src/Worklane.Infrastructure/Persistence/Migrations/20260714120745_InitialCreate.cs:41`). No SQL default. The snapshot still has the column and does not index it (`WorklaneDbContextModelSnapshot.cs:662-663`). Unique email is `EmailIndex` on `NormalizedEmail` (`20261025155535_MakeEmailIndexUnique.cs:17-21`).

The only writer of `true` in `src` is the demo seed (`src/Worklane.WebApi/Seed/DevSeeder.cs:44-54`). Register does not set the flag, so accounts created through the API are stored as `false`. No migration after `InitialCreate` updates the column.

Membership audit today is `WorkspaceMembershipEvents` (`src/Worklane.Domain/Entities/WorkspaceMembershipEvent.cs:9-23`) with actions `RoleChanged`, `MemberRemoved`, `MemberAdded` only (`src/Worklane.Domain/Enums/WorkspaceMembershipAction.cs:9-13`). That table always has a workspace. It cannot store "verification sent". No user-delete or PDPA erasure path exists in `src`. `AppUser` has no created-at column.

## 3. Error order used below

Agreed check order, first match wins. This matches the pipeline in `docs/conventions-api.md:194-207` and `Program.cs:249-252`, with the two extra slots called out.

1. **401** — missing or invalid JWT (empty bearer challenge), or `unauthorized` from the filter (`RequireWorkspaceRoleAttribute.cs:40-44`). Login and refresh stay on `unauthorized`.
2. **429** — layer-1 limiter, before the action. `rate_limited` + `scope`. `Retry-After` header only.
3. **413** — `request_too_large`.
4. **404** — unknown resource, where the route has one (a bad verification token).
5. **400** — body cannot bind (`invalid_request_body`), or `invalid_route_id`.
6. **404 non-member** — `RequireWorkspaceRole` uses the same `not_found` body as a missing id (`RequireWorkspaceRoleAttribute.cs:94-100`).
7. **403** — member whose role is too low (`workspace_access_denied`), or **new** `email_not_confirmed` where an option gates a signed-in user.
8. **400 validator** — `validation_failed` plus `errors` and `fieldCodes`.
9. **429 workspace** — not used. Workspace create's 429 is the early layer-1 limiter (`scope` `user`), which the catalog already orders before 400 on that route. A second, later 429 is not added.
10. **422** — not used. The catalog and `AppException` subclasses have no 422. "Not verified" is 403 (or the uniform 401 on login). Do not add a 422 for it.

**New** codes, to be added to `docs/api-error-codes.md` in the implementation PR only:

| code | HTTP | detail |
|---|---|---|
| `email_not_confirmed` | 403 | `Confirm your email before continuing.` |
| `email_verification_invalid` | 404 | `This verification link is invalid or has expired.` |

`email_verification_invalid` is one body for unknown, expired, and already used, the same rule as invite accept. It is a specific code so Web can localize it. `not_found` stays the membership and missing-id code.

## 4. Shared design (options A and B)

Option C does not send a signup verification message. Sections 4.1–4.3 are the machinery for A and B. Sections 4.4–4.6 apply to all three, because H1 will read `EmailConfirmed` for every current account.

### 4.1 Send and resend

**Sender.** Keep `IEmailService`. No `IEmailSender<AppUser>` (that type is not registered). A vendor HTTP client, including Resend the product, would be **new** and would replace the registration at `DependencyInjection.cs:82`. Nothing in the repo calls Resend today. The first implementation can stay on `SmtpEmailService`.

**Send on register (A and B).** Today the register callback saves the user, workspace, and membership (`AuthService.cs:115`) and issues tokens (`:116`) inside the transaction that `WorkspaceSlug.InsertAsync` commits when the callback returns (`WorkspaceSlug.cs:64-66`). Create the verification token (section 4.2) and send only after that commit, so a failed send does not roll the account back. Option A skips `:116` (no refresh row). Option B keeps it. Invite send is the same shape: it runs after its own commit (`WorkspaceService.cs:197-217`).

**Message.** Fixed subject in `en` and `th` (no workspace name, no user-typed text in the subject). Body is two HTML strings, **new**, chosen from `user.PreferredLanguage` (`WorkspaceLocale` only returns `en` or `th`). HTML-encode the display name, same as `WorkspaceService.cs:207`. The link is `Frontend:BaseUrl` plus a **new** path such as `/verify-email?token=`, built the way `InviteLinks.ForToken` builds the invite link, so the host is the validated frontend origin (`FrontendSettingsValidator.cs:24-32`). The API accepts the token in a JSON body, not a query string, so the API access log does not record it. The raw token appears in the SPA URL, which is the same exposure invite links have today.

**Verify endpoint (new).** `POST /api/auth/verify-email`, `[AllowAnonymous]`, body `{ "token": "..." }`. Validator: token not empty. Success **204**. Failure **404** `email_verification_invalid` for unknown, expired, and used. The response body does not echo the address. Confirm sets `EmailConfirmed = true` and consumes the token (section 4.2).

**Resend endpoint (new).** `POST /api/auth/resend-verification`, `[AllowAnonymous]`, body `{ "email": "..." }`. Validator: same email rules as register. Success is always **204** with an empty body, for an unknown address, an already confirmed address, and a real send. The handler still charges the limiter when the address is unknown (section 4.3). It sends only when a user exists and `EmailConfirmed` is false. It does not create a user. Register's **409** `email_already_registered` stays as it is (review L2 calls that an accepted product trade-off).

**Failure and retry.** No retry loop inside the request, and no background queue exists. If `SendAsync` throws, log a warning and still return the success status the endpoint already promised (201 for register, 204 for resend). The client retries through resend. Do not return the token to the client. Invite's "response includes the link" fallback (`WorkspaceService.cs:219`) does not apply here.

**Webhook / bounce.** Optional and **new**. No inbound mail route exists. A and B work without it. Whether v1 includes it is an open question.

**Logs.** Log the user id and the event name (`verification sent`, `verification send failed`, `verification confirmed`). Do not log the raw token, the token hash, the HTML body, or the SMTP password. The existing sender log includes the recipient address (`SmtpEmailService.cs:39`) and the invite logs include it too (`WorkspaceService.cs:191-194`, `:216`). New verification logs should not copy that. The address is personal data (section 4.6).

### 4.2 Token

Two mechanisms. The owner picks.

**Identity email-confirmation token.** `UserManager.GenerateEmailConfirmationTokenAsync` / `ConfirmEmailAsync`, provider already registered (`DependencyInjection.cs:50`), protected by the Worklane key ring (`DataProtectionRegistration.cs:18`). No new table. The repo does not set `TokenLifespan`; set it explicitly if this mechanism is chosen, so lifetime is not the framework's implicit one day. Single-use is not a property of this token until the security stamp changes. `ConfirmEmailAsync` (framework) does not by itself rotate the stamp. Call `UserManager.UpdateSecurityStampAsync` on confirm so the link dies, and call it again when an email-change or password-reset feature exists. Those features do not exist today (`AuthController.cs` is only register, login, refresh, logout). A stamp rotation also kills outstanding refresh sessions, which may be more than this ticket wants. Tests that use the Testing environment get an ephemeral key ring (`DataProtectionRegistration.cs:22-27`), so a token minted on one host will not confirm on another.

**Custom hashed token table (new).** Same shape as invite and refresh tokens: 32 random bytes (`InviteTokens.ByteLength`), store SHA-256 hex only (`InviteTokens.Hash`), columns for user id, hash (unique), `ExpiresAt`, `CreatedAt`, `ConsumedAt`. No raw-token column. The invite plaintext column was dropped in `20261028094519_HashInvitationTokens` (`src/Worklane.Infrastructure/Persistence/Migrations/20261028094519_HashInvitationTokens.cs:54-55`). Confirm sets `ConsumedAt` and `EmailConfirmed` in one save. A second use, a mismatch, and an expiry share the 404 body. On a future email change or password reset, consume every unconsumed row for that user. No such endpoint exists today; the hook is a requirement on that later ticket.

**Suggested lifetime: 24 hours.** Long enough to cover a workday and a mailbox the user checks late; short enough that a forwarded or leaked link is not a week-long credential. Invite links are 7 days because a second person has to act (`WorkspaceService.cs:183`). This link is the same person who just signed up, and resend covers a missed message. 24 hours also matches the protector's usual default, so the two mechanisms do not surprise operators. The number is a suggestion; the owner sets it.

Either mechanism: one live token per user. A resend consumes the previous row (custom table) or rotates the stamp / issues a new protector token and treats older links as invalid (Identity). Prefer the custom table when single-use must be obvious in the database. That preference is a suggestion.

### 4.3 Resend limits

Use the layer-1 limiter. **New** policy, for example `email-verification`, on verify and on resend. Defaults, as examples the owner can change: **1 per minute** and **5 per day** for a given address, and the same numbers per client IP. Config keys would be **new** under `RateLimit`, following `AuthRateLimitSettings`.

`FixedWindowRateLimiter` is one window, and an action takes one policy (`Program.cs:126-139`). A minute window and a day window in one rejection need a **new** limiter that checks both windows and still writes the current 429 body. `Retry-After` is the sooner window. `RateLimitScope` (`Program.cs:172-176`) returns `ip` only for the policy name `auth`. The new policy has to return `ip` for the IP window. Suggested `scope` for the per-address window is `user`, including when the address has no account, so the scope does not reveal that the account exists.

Keys: client IP from `ForwardedClientIp.PartitionKey` (do not trust a spoofed forwarding header; that helper already implements the rule). Address key: Identity's normalized email, hashed, so the limiter key is not the raw address. Charge the address bucket before deciding whether to send, including for an unknown address.

The 429 body stays `rate_limited` plus `scope`. No counts in the body.

Verify gets the IP window so a stolen or guessed token cannot be sprayed. A 32-byte token makes guessing irrelevant; the limit is still cheap.

Register stays on the existing `auth` policy (10/min per IP). The verification send on register counts as that request plus one address charge, so a script cannot turn register into an unlimited mailer.

Uniform resend responses are in section 4.1. Residual timing: a real SMTP send is slower than a no-op. Return 204 without waiting for SMTP (hand the send to the request's existing fire path, or accept the timing leak and rely on the limiter). There is no queue in the repo. The review already accepts a timing leak on login (L2) inside the same IP limit.

### 4.4 Existing users

Every API-registered user is `EmailConfirmed = false` (section 2). The demo user is `true`. Shipping H1 without a data change means every current real user stops being auto-added and gets a token email instead. That is H1's rule. This ticket chooses how the flag is initialized.

**Option — grandfather.** One later migration (not in this PR). `Up`: insert into a **new** table the user ids where `EmailConfirmed` is false, then set those rows to true. `Down`: set `EmailConfirmed` false for ids still in that table, then drop the table. Ids already true (the demo seed) are not in the table, so `Down` leaves them true. Users created after `Up` are not in the table, so `Down` does not clear a real confirmation. `Down` is unsafe if it is a blanket `SET false`: that would unconfirm people who verified after the migration.

DB impact: one update of every currently unconfirmed `AspNetUsers` row (WAL for those rows). `EmailConfirmed` is not indexed, so no index maintenance. The side table is one uuid per flipped user. No invitation rewrite. Do not run it here. Conventions allow one migration in the implementation PR.

**Option — force verify on next login.** No migration and no row rewrite. The next login (option A) or the next gated invite (option B) sees `false`. The demo seed stays usable. Under H1, current users are not auto-added until they verify.

`AppUser` has no created-at, so a grandfather cannot be expressed as "rows created before this migration" without the side table above or a **new** timestamp column.

### 4.5 Audit

Who and when, for sent and for confirmed.

`WorkspaceMembershipEvents` cannot hold this (section 2). Two shapes:

- **New** append-only table: user id, event (`Sent` or `Confirmed`), `OccurredAt`. No token, no address. The actor is that user.
- Structured log lines only (section 4.1). They die with the log sink.

The table is the suggestion, because "who and when" has to outlive a container restart. IP is personal data; leave it out unless the owner asks for it.

### 4.6 PDPA

The email address is personal data. It is already stored on `AspNetUsers.Email` and `Invitations.Email`, and it is already written to logs (`SmtpEmailService.cs:39`, `WorkspaceService.cs:191-194`). Verification adds more messages to that address. Store the token only as a hash. Do not add the address to the new audit rows.

**Retention of unverified accounts is an option, not a decision.** Example: delete an account that is still unconfirmed N days after it was created. N is the owner's. The repo has no created-at on the user and no delete-user path, so a purge needs a **new** timestamp and a **new** job. Register also creates a workspace and an Owner membership (`AuthService.cs:97-113`); a purge has to define what happens to that workspace (and to any cards in it) before anyone writes the job. This note does not design that job.

### 4.7 Web (brief)

Worklane-Web, separate repo, en and th. Shared pieces if A or B is chosen:

- A verify screen on the new frontend path. It POSTs the token and treats 404 `email_verification_invalid` as an expired or used link, with a resend form.
- Resend shows the same success copy for every 204. On 429 it reads `Retry-After` (already exposed) and does not expect a wait field in the body.
- Copy lives in the Web i18n files. The API `detail` stays English (`docs/conventions-api.md`).

## 5. Option A — Require verification before login

### User flow

1. `POST /api/auth/register` creates the user (`EmailConfirmed` false), the personal workspace, and the Owner membership, same as today. It does not call `IssueTokensAsync` (`AuthService.cs:116`), so no refresh row is committed. It sends the verification message after commit (section 4.1). It does **not** return an access token or a refresh token. Returning tokens would be a login.
2. The user opens the link and `POST /api/auth/verify-email` confirms the address.
3. The user signs in with `POST /api/auth/login`. Until then, login and refresh do not issue tokens.
4. Resend uses section 4.1. There is no session yet, so resend is by email.

The personal workspace exists before the first successful login. That matches the current register transaction. Leaving the row uncommitted until the link is clicked is a different flow; it is question 2 below, because of squatting.

### API changes

Register success becomes **201** with a **new** body that has the user id, email, display name, preferred language, and `emailConfirmed: false`, and has no tokens. That is a breaking change for Web. `IAuthService.RegisterAsync` today returns `AuthTokensResponse` (`src/Worklane.Application/Auth/Interfaces/IAuthService.cs:7`).

Login, after the existing password check: if the password is wrong or the user does not exist, keep **401** `unauthorized` / `Invalid credentials.` (M1 keeps this body when locked). If the password is right and `EmailConfirmed` is false, the owner chooses one of:

- same **401** body as a bad password (no new oracle), or
- **403** `email_not_confirmed` (tells the client the password was correct and the account exists).

Refresh: if the user is unconfirmed, do not issue a new access token. Suggested companion: **401** with the existing refresh detail `Refresh token is invalid or expired.` (`AuthService.cs:138`). A confirmed user's refresh is unchanged.

Verify and resend: section 4.1. Order on those routes: 429, 413, 400 `invalid_request_body`, 400 `validation_failed`, then 404 on verify or 204 on resend. No 401 (anonymous). No 422.

No change to invite or accept routes. An unverified user has no token, so they cannot call them. H1 still decides auto-add from `EmailConfirmed`.

### Web

Register no longer stores a session. The next screen is "check your email", plus resend. Login, if the API returns the distinct 403, shows a confirm-your-email state; if the API keeps 401, login stays on the generic credentials error and resend is a separate screen. The verify screen is section 4.7.

### Effort

Largest of the three. Breaks the register 201 contract. Touches `AuthService` register, login, and refresh; `AuthController`; a new response record; verify and resend; the mail and limiter from section 4; the catalog; Web register and login. The personal-workspace transaction can stay.

### Security

- **Enumeration.** Register 409 stays (L2). Resend is a uniform 204. A distinct 403 on login confirms the password and the account; the uniform 401 does not. M1's locked-out 401 must stay that same body either way.
- **Squatting.** The attacker who registers the victim's address owns the password, and the victim gets 409 on their own signup. If the victim then clicks the verification link, `EmailConfirmed` becomes true while the attacker's password still works. H1 will then auto-add that account. Confirmation has to take the password away from the registrant or the account must not exist until the mailbox owner sets the password (question 2).
- **Phishing.** The message is a fixed Worklane subject and a link on `Frontend:BaseUrl` only. It must not include a workspace name. This is a second mail stream beside invites.
- **Mail abuse.** Bounded by the `auth` IP limit plus section 4.3. Invite relay (L3) is unchanged.
- **Takeover via an unverified email.** Login is refused, so the attacker cannot use the personal workspace or call the API until the link is consumed. After a naive confirm, they can, and H1 will trust the flag.
- **H1 invite token.** H1 sends a token instead of auto-adding while the flag is false. The attacker does not have the mailbox, so they cannot accept. The victim has the invite token and does not have the password, so they cannot accept either, until question 2 gives the mailbox owner a way in. A and H1 together deadlock the squat unless that question is answered.

## 6. Option B — Login allowed; invites blocked until verified

### User flow

1. Register stays as it is: **201** with tokens, personal workspace, `EmailConfirmed` false, plus the verification message.
2. The user can sign in, refresh, and use workspaces they already belong to (the one register just created).
3. Until they verify, they cannot accept an invite, cannot create an invite, and H1 will not auto-add them into someone else's workspace.
4. They verify through the same link and `POST /api/auth/verify-email`. The next login or refresh returns `emailConfirmed: true`. The access token itself has no such claim (`JwtTokenService.cs:28-34`); Web reads the DTO, not the JWT. A token issued before confirm stays valid until its 15-minute expiry.

Whether `POST /api/workspaces` (another workspace they would own) is blocked is question 4. The option as stated blocks joining, accepting, and creating invites.

### API changes

**New** field `emailConfirmed` on `UserDto` (`AuthDtos.cs:11`), so register, login, and refresh tell Web the flag. Existing clients that ignore unknown JSON fields keep working; clients that require an exact shape need a Web change.

Gates, **new**, checked on the caller:

- `POST /api/workspaces/{id}/invitations` — after the role filter's 403 and before `ValidateAndThrowAsync` (`WorkspacesController.cs:67` runs the validator first today; the gate moves ahead of it so 403 wins over 400 `validation_failed`). **403** `email_not_confirmed`. An unverified caller never reaches the existing-user branch, so this call stops returning `Id = Guid.Empty` for them.
- `POST /api/invitations/accept` — order: 401, 429 if this route gains the layer-1 policy (it has none today), 413, 400 body, 400 validator, then the service. The service keeps today's 404 for a bad, expired, used, or wrong-account token, then **403** `email_not_confirmed` when the token is usable and the caller is unverified. A 32-byte token means the 404-versus-403 split is not a practical oracle. H1's auto-add change stays in `InviteMemberAsync` and is not re-specified here.

Verify and resend: section 4.1. An already signed-in user may also call resend; the address in the body is ignored and the session user's address is used, so a signed-in caller cannot send to someone else. Anonymous resend remains for the user who has no session.

Login and refresh succeed for an unconfirmed user. No 403 on those routes.

### Web

No change to the register-then-enter-the-app path. A banner while `emailConfirmed` is false, plus the verify screen and resend. Invite and accept screens handle 403 `email_not_confirmed` with that banner, not the generic permission error (`workspace_access_denied` / `forbidden`).

### Effort

Middle. Register's 201 token contract stays. Adds the DTO field, two gates, verify, resend, mail, limiter, catalog, and a Web banner. Does not change `LoginAsync`'s success path.

### Security

- **Enumeration.** Unchanged on register (409) and login (401, plus M1). Resend is a uniform 204. The invite gate returns 403 before the existing-email lookup, so an unverified inviter no longer gets the `Guid.Empty` signal. A verified inviter still does; closing that is not this ticket.
- **Squatting.** The attacker can sign in and use the personal workspace under the victim's address. They cannot be auto-added (H1) and cannot accept the token they cannot read. The victim still cannot register (409) and still cannot accept, because accept requires a session whose email matches (`WorkspaceService.cs:231`). The same password problem as option A: if the victim confirms the attacker's row, the attacker's password becomes a confirmed account and H1 will trust it (question 2).
- **Phishing.** Same as section 4.1. The invite mail the attacker can still send from their own workspace is L3, not this option.
- **Mail abuse.** Signup verification is bounded by section 4.3. Creating invites is blocked until they verify, which removes the L3 relay for unverified accounts only. A confirmed account can still relay until L3 is fixed.
- **Takeover via an unverified email.** The cross-workspace takeover in the review is H1's fix (no auto-add while the flag is false), which this option relies on. Inside their own workspace the unverified user is a normal Owner. That is impersonation of the address on Worklane, limited to a workspace they created.
- **H1 invite token.** The token is what proves the mailbox for someone who is not yet confirmed. Option B does not treat that token as confirmation: accept is refused until a separate verification. The person who can read the invite mail still needs a session. See the deadlock in option A's last bullet. Question 2 is what breaks the deadlock.

## 7. Option C — Verify only when an invite is accepted

### User flow

1. Register and login stay as they are today. No verification message is sent. `EmailConfirmed` stays false.
2. The user works in the workspace register created.
3. When they accept an invitation, the accept that already requires a live token and an email match sets `EmailConfirmed = true`.
4. There is no verify screen and no resend. A user who never accepts an invite stays unconfirmed. H1 will keep sending them token invitations instead of auto-adding them.

### API changes

No new route. In `AcceptInvitationAsync`, after the checks that throw 404 and after the membership insert is decided, set `EmailConfirmed` on the accepting user in the same `SaveChanges` as `AcceptedAt` (`WorkspaceService.cs:276-277`). A failed accept must not set the flag. Auto-add (H1) is still based on the flag before this accept; this option does not change that method.

`UserDto.emailConfirmed` is still worth adding so Web can show the state. It is not required for the proof itself.

Error order on accept is unchanged: 401, 413, 400, 404 `not_found` with the current invitation detail, then the existing 403 from `EnsureCanInvite`. No `email_not_confirmed`. No 422.

Section 4.1's mail, section 4.3's limits, and the verify endpoint are not built for this option.

### Web

No verify screen. If the DTO field is added, a badge is optional. Accept is unchanged from the user's point of view.

### Effort

Smallest. One flag write on the accept path, plus the DTO field if Web should see it, plus whichever existing-user migration the owner picks. No mail templates and no new limiter. Depends on H1 being present so the flag means something on the next invite.

### Security

- **Enumeration.** Unchanged (register 409, login timing, invite `Guid.Empty`).
- **Squatting.** Unchanged and unresolved. The attacker keeps the account and the password. The victim cannot register. The victim cannot accept the invite that H1 emails them, because accept requires the attacker's session (`InvitationsController.cs:29-34`, `WorkspaceService.cs:231`). The flag never flips. The address stays occupied.
- **Phishing.** No new message. Invite mail stays as it is, including the attacker-chosen workspace name in the subject (`WorkspaceService.cs:212`), which is the existing L3 content.
- **Mail abuse.** No new stream. L3 is unchanged, including for unverified Owners, because this option does not gate `InviteMemberAsync`.
- **Takeover via an unverified email.** H1 is what blocks auto-add. Option C does not block login, invite create, or the personal workspace. After a real accept, the flag becomes true and a later invite may auto-add. That accept is real proof: the caller had the password and the mailed token. It is not proof at signup.
- **H1 invite token.** This option is "the invite token is the proof", and the current accept route only lets the account that already owns the email use that proof. The mailbox owner who does not know the password never gets to be the caller. C records proof for the account holder. It does not transfer the account to the mailbox.

## 8. Suggestion (pending the owner's choice)

**Suggested option: B.** It puts the mailbox check on the trust boundary the review cares about: joining a workspace, accepting an invite, and sending an invite. Register can keep returning tokens, which is the contract Web has now, and the user can open the workspace register just created. H1 (in progress) already wants `EmailConfirmed` on the auto-add path; B is the product rule that makes the same flag block the other invite directions, without taking away login.

**A** is the stronger rule if an unverified account must not call the API at all. It is a larger break (register stops returning tokens) and it still needs question 2.

**C** is the smallest change and it does not give the mailbox owner a session, so it does not fix squatting and it does not stop an unverified Owner from sending invite mail.

Also suggested, still pending the owner:

- Custom hashed token table, 24-hour TTL, single-use (section 4.2).
- Grandfather current `false` rows with the side-table `Down` (section 4.4), so H1 does not suddenly token-gate every existing customer. Force-verify is the alternative if those customers must prove the mailbox.
- Audit table rather than logs only (section 4.5).
- On confirm, do not leave the registrant's password in place (question 2). This matters for A and for B. It is the difference between "the mailbox owner confirmed" and "the victim armed the attacker's account".

## 9. Open questions for the owner

1. Which option: A, B, or C?
2. When a link is confirmed, what happens to the password the registrant chose? Choices: the mailbox owner must set a new password and refresh tokens are revoked; or the user row is not created until the link is consumed and the password is set then; or the password is left as-is (this arms a squatted account once the victim clicks). A and B need an answer. C never gives the mailbox owner that click unless they already know the password.
3. Existing users: grandfather, or force verify on next login?
4. Under B, does `POST /api/workspaces` stay available before verification?
5. Under A, is an unconfirmed login the uniform 401, or 403 `email_not_confirmed`?
6. Are 1/minute and 5/day the right resend caps, per address and per IP?
7. Stay on SMTP, or add a vendor sender (Resend or otherwise) behind `IEmailService`?
8. Is a bounce webhook in v1?
9. Purge unverified accounts after N days? If yes, what is N, and what happens to the personal workspace?
10. Should the audit row store IP? Default in this note is no.

## 10. Test cases (xUnit)

Implementation later, not this PR. Project: `tests/Worklane.IntegrationTests`. Folder: `Auth/`. Host: `WebApplicationFactory`. A **new** fake `IEmailService` registered in the test host (none exists in `tests` today). Assert problem bodies with `TestAuthHelper.JsonOptions` where JSON is read. Cross-tenant cases stay on the workspace routes: non-member **404** `not_found`, low role **403** `workspace_access_denied`, and those must still win over `email_not_confirmed`.

### Shared (A and B)

- Confirming a valid token sets `EmailConfirmed` and returns 204. The same token then returns 404 `email_verification_invalid`.
- An expired token, a tampered token, and a token whose hash is unknown return that same 404 body.
- A second resend consumes the previous token (custom table) or otherwise makes the older link return that 404.
- Resend for an unknown address, a confirmed address, and an unconfirmed address all return 204 with an empty body. The fake sender is called only for the unconfirmed address.
- Over the per-IP window and over the per-address window: 429, `code` `rate_limited`, the matching `scope`, a `Retry-After` header, and no `retryAfterSeconds` or limit numbers in the body. The unknown address consumes the address window too.
- The fake sender's body contains no raw token when the test inspects logs. The HTML for `PreferredLanguage` `th` is the Thai string; any other language is English.
- A throwing sender on resend still returns 204 and does not persist a raw token.
- Register's 409 `email_already_registered` is unchanged when the email exists.
- `dotnet test` covers the ephemeral Data Protection host only if the Identity token mechanism is chosen: a token from one factory must not be required to survive a second factory.

### Option A

- Register returns 201 without `accessToken` or `refreshToken`, and the user cannot call `GET /api/workspaces` with no token (401).
- Login with the right password before confirm returns the chosen body (401 or 403, matching question 5). Login with the wrong password returns 401 `Invalid credentials.` in both the confirmed and unconfirmed cases.
- After confirm, login returns tokens and `emailConfirmed: true`.
- Refresh with a token that belongs to an unconfirmed user returns 401 and does not return an access token. Refresh after confirm rotates as it does today.
- A locked-out account, once M1 has landed, still returns the M1 401 body and not `email_not_confirmed`.

### Option B

- Register and login return tokens with `emailConfirmed: false`. The caller can `GET /api/workspaces` and sees the personal workspace.
- `POST /api/workspaces/{id}/invitations` as that Owner returns 403 `email_not_confirmed` before validation (an empty email is still 403, not 400).
- A non-member of that workspace still gets 404 `not_found`. A Member still gets 403 `workspace_access_denied`, not `email_not_confirmed`.
- Accept with a bad token returns the current invitation 404. Accept with a valid token while unconfirmed returns 403 `email_not_confirmed` and does not set `AcceptedAt`.
- After confirm, invite create and accept follow today's rules, and H1's auto-add is allowed only when the invitee's flag is true (assert the flag; do not re-test H1's patch beyond that).
- `POST /api/workspaces` follows the answer to question 4.

### Option C

- Register leaves `EmailConfirmed` false and sends no mail.
- A failed accept (unknown token, wrong account, expired) leaves `EmailConfirmed` false.
- A successful accept sets `EmailConfirmed` true in the same outcome as `AcceptedAt`.
- A second invite of that now-confirmed email follows H1's auto-add rule. An unconfirmed email does not get a membership row from invite create.
- Login of an unconfirmed user still returns tokens.

### Migration (whichever option)

- Grandfather, if chosen: a user inserted as `EmailConfirmed` false is true after `Up`; a user inserted as true stays true; `Down` sets false only for the ids `Up` recorded.
- Force-verify, if chosen: no migration, and an existing `false` row is the one the option's gate or login sees.
- These run against Testcontainers only. Nothing in this design is applied to a shared database.
