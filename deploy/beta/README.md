# Worklane beta on Oracle Cloud Always Free

This directory is the beta deploy for about 10 users. It is a plan you can execute later. Nothing in this repo provisions a VM, a Cloudflare account, an R2 bucket, or any other service, and it commits no real secrets.

The dev stack is still the repo-root `docker-compose.yml`. Do not point that file at beta data, and do not point this file at the dev database.

| | Dev (repo root) | Beta (this directory) |
|---|---|---|
| File | `docker-compose.yml` | `deploy/beta/docker-compose.yml` |
| Project | whatever Compose picks | `worklane-prod` and `worklane-staging` |
| Postgres port | published on the host | not published |
| JWT | the committed dev value | a new value per environment, from the env file |
| Environment | Development (migrates and seeds) | Production or Staging (does neither) |

One Ampere A1 VM (2 OCPU / 12 GB, Ubuntu, `linux/arm64`, Singapore) runs both projects. Cloudflare Tunnel is the only way in. Postgres, the API, and nginx listen on the Compose network only.

Security constraints for this change:

- No GitHub Actions job here deploys. `.github/workflows/docker-image.yml` builds the API image for `linux/amd64` and `linux/arm64` and does not push it. A deploy workflow waits until WL-63 is merged.
- Beta compose never sets `ASPNETCORE_ENVIRONMENT=Development`. Prod is `Production`. Staging is `Staging`.
- `Jwt__Secret` in the beta compose is only `${JWT_SECRET}` from the git-ignored env file. The file has no secret value.
- No service publishes a host port, so nothing listens on `0.0.0.0`. Postgres is not published. A future loopback mapping has to be `127.0.0.1:<port>:<container>`.
- The stack does not reference rustfs or seaweedfs. Attachments stay on a local volume until B-18.

## WL-63 gate

Do not start the tunnel, and do not put a public hostname on it, until WL-63 is actually done:

1. The image you deploy rejects the JWT signing secret that is still in `docker-compose.yml` and `appsettings.Development.json`. That value is burned. It is in git history. Do not reuse it for dev, CI, staging, or prod.
2. Staging and prod each have their own new secret (`openssl rand -base64 64 | tr -d '\n'`, at least 32 bytes after base64-decode).
3. You have checked, on that image, that a token signed with the burned secret gets **401** on `/api/*` and on `/hubs/notifications`.

This PR does not implement that API behaviour. `scripts/preflight.sh` only refuses to boot when the env file still contains the burned value (compared by SHA-256 in `leaked-jwt-sha256.txt`). That is necessary and not sufficient.

`scripts/up.sh` also requires a git-ignored file `deploy/beta/WL63_DONE` whose first word is `yes`, written by a person after the 401 check above. Example:

```text
yes 2026-10-18 leaked token returns 401 on /api and /hubs
```

## What you get

```text
deploy/beta/
  docker-compose.yml          one stack, two project names
  .env.example                placeholders only
  r2-lifecycle.example.json   S3 lifecycle document, not applied
  cron.example
  leaked-jwt-sha256.txt
  scripts/
    up.sh                     preflight + WL-63 gate + compose up
    preflight.sh
    check-env-isolation.sh
    check-compose.sh          CI and local render check
    init-db-roles.sh
    ef-migrate.sh             pre-migration dump, then dotnet ef
    pg-dump-to-r2.sh
    restore-from-r2.sh
    sync-attachments-to-r2.sh stopgap volume mirror until B-18
```

Memory limits below assume both projects are up on the 12 GB VM. The caps sum to about 5.7 GB, which leaves room for the OS, page cache, and a migrator or `pg_dump`.

| Service | prod | staging |
|---|---|---|
| postgres | 2 GB / 0.60 CPU | 1 GB / 0.30 CPU |
| api | 1536 MB / 0.70 CPU | 768 MB / 0.35 CPU |
| web | 128 MB / 0.10 CPU | 96 MB / 0.05 CPU |
| cloudflared | 128 MB / 0.10 CPU | 96 MB / 0.05 CPU |

CPU values are ceilings. They can add up to more than 2 OCPU. Do not add a fake CPU load to stay above Oracle's idle threshold. If Oracle warns about reclaiming an idle VM, use the DigitalOcean runbook below or upgrade the Oracle account to Pay As You Go and set a budget alert at $1. Whether Pay As You Go exempts the idle-reclaim rule was not confirmed in the plan.

## 1. VM

Do this in the Oracle console. This repo cannot do it for you.

1. Home region **Singapore**. Always Free home region cannot be changed later. If Ampere A1 capacity is unavailable, stop and use the fallback note at the end of this file. Do not pick another home region to get a VM.
2. Shape `VM.Standard.A1.Flex`, 2 OCPU, 12 GB RAM, Ubuntu 24.04 aarch64.
3. Boot volume 50 GB plus a block volume (100 GB is enough). Attach the block volume.
4. Put an SSH key on the instance. Prefer OCI Bastion so the subnet security list has **no** public ingress, including port 22.
5. Security list and network security group: ingress empty. Egress allows outbound TCP 443 (the tunnel, R2, Resend, package mirrors). Do not open 80, 443, or 5432.
6. On the VM, if you must keep SSH on a public IP for the first hour, `ufw default deny incoming`, `ufw default allow outgoing`, `ufw allow from <your-ip> to any port 22`, then `ufw enable`. Close 22 once Bastion works.

On the VM:

```bash
sudo apt-get update
sudo apt-get install -y unattended-upgrades age
sudo dpkg-reconfigure -plow unattended-upgrades
```

Install Docker Engine from Docker's apt repository (Ubuntu's `docker.io` is optional; Compose v2 is required). Confirm:

```bash
docker compose version
uname -m   # aarch64
```

Format the block volume only if it is empty, mount it at `/var/lib/docker`, and point Docker's data-root there so images and named volumes survive a boot-disk rebuild. Record the volume OCID.

Clone this repo to `/opt/worklane` (no secrets in git). Clone `Worklane-Web` next to it or elsewhere. Build the web image on the VM:

```bash
docker build --platform linux/arm64 -t worklane-web:beta /path/to/Worklane-Web
```

Confirm that image's nginx proxies `/api` and `/hubs` to `http://api:8080` and that `/hubs` sets `Upgrade` and `Connection "upgrade"`, with `proxy_buffering off` and a long read timeout. This repo does not contain that nginx config. A missing upgrade header breaks SignalR even when the tunnel is fine.

## 2. Env files

```bash
cd /opt/worklane/deploy/beta
cp .env.example .env.prod
cp .env.example .env.staging
chmod 600 .env.prod .env.staging
```

Edit both. Staging changes are the commented block at the bottom of `.env.example` (project name, `Staging`, hostname, smaller limits). Generate a new secret for every `replace-me` that is a password, token, or key. Prod and staging must differ for JWT, every database password, the tunnel token, the SMTP password, and the R2 secret keys.

`scripts/check-env-isolation.sh .env.prod .env.staging` checks that. `scripts/preflight.sh --up .env.prod` checks placeholders, password shape, and the burned JWT.

Leave the files only on the VM (mode 600, owner root). A copy of the secrets belongs in the password manager, not in git and not in a GitHub issue.

## 3. Cloudflare Tunnel and DNS

One tunnel per environment, so each has its own token.

1. Domain on Cloudflare DNS (the registrar can stay Porkbun; nameservers point at Cloudflare).
2. Create a tunnel. Copy the token into `CLOUDFLARE_TUNNEL_TOKEN`. Do not commit it.
3. Public hostname for prod: `app.<domain>` → `http://web:80`.
4. Public hostname for staging: `staging.<domain>` → `http://web:80` on the **staging** tunnel.
5. The hostname `web` is the Compose service. Each cloudflared container is on its own network (`worklane-prod-net`, `worklane-staging-net`), so `web` resolves to that project's nginx.

The API is `https://app.<domain>/api` and SignalR is `wss://app.<domain>/hubs/notifications` on the same host. That matches the Angular app and avoids CORS. A separate `api.<domain>` pointed at `http://api:8080` is not wired: the API has no forwarded-headers setup and the web client is same-origin.

Cloudflare SSL mode **Full (strict)** once the edge certificate is active. Cache rule: bypass `/api/*` and `/hubs/*`. Hashed static files can cache. Do not cache `index.html`.

HSTS during beta: start at `max-age=300` for a week, then one day, then 30 days. Do not preload, and do not add `includeSubDomains` until every subdomain is HTTPS.

Staging hostname: Cloudflare Access (free tier, up to 50 users) plus `noindex`. The Angular `index.html` change for `noindex` is a web-repo follow-up; Access is the control that matters.

## 4. R2

Two buckets. Apply nothing from this repo automatically.

| Bucket | Contents | Key |
|---|---|---|
| `worklane-backup` | age-encrypted `pg_dump` files | backup token only |
| `worklane-attachments` | stopgap mirror of the local volume | a different token |

Endpoint: `https://<account_id>.r2.cloudflarestorage.com`, region `auto`.

Lifecycle for the backup bucket is `r2-lifecycle.example.json` (S3 `PutBucketLifecycleConfiguration` body):

| Prefix | Delete after |
|---|---|
| `daily/` | 35 days |
| `weekly/` | 91 days (13 weeks) |
| `monthly/` | 365 days |
| `pre-migration/` | 30 days |

Apply it in the dashboard or with the AWS CLI against the R2 endpoint when you are ready. Also add a [bucket lock](https://developers.cloudflare.com/r2/buckets/bucket-locks/) per prefix for the same duration, so a leaked token cannot delete dumps early. A lock longer than the lifecycle rule blocks the lifecycle delete (lock wins). Monthly retention and PDPA still need a legal look, as in the SA notes.

Create an age key on a machine that is not the VM. Put the **public** recipient in `BACKUP_AGE_RECIPIENT`. Put the private key in a password manager and an offline copy. It must not live on Oracle and must not live in Cloudflare. `BACKUP_AGE_IDENTITY_FILE` is only present on a machine while you are restoring, mode 600.

Whether an R2 token can be scoped to one bucket was not confirmed. Create the most limited token the dashboard allows, one per bucket.

## 5. Resend, Better Stack, Grafana, Sentry

**Resend.** Verify the sending domain. Add the SPF TXT (one `v=spf1` record only), the DKIM CNAMEs, and the return-path records Resend shows. DMARC, start here and tighten after a few weeks of reports:

```text
_dmarc.<domain>  TXT  "v=DMARC1; p=none; rua=mailto:dmarc@<domain>; adkim=s; aspf=r"
```

Then `p=quarantine; pct=25`, then `pct=100`, then `p=reject`.

Free tier is 100 messages/day and 3,000/month. SMTP host `smtp.resend.com`, port 587, username `resend`, password is the API key. Oracle's docs say port 25 is blocked; whether 587 works was not confirmed. Test it before inviting users.

The API sets the From address to `Email:SmtpUsername`. With username `resend`, mail will not be accepted by a real domain. Sending mail is a follow-up until From is a separate setting. Configure the DNS now anyway.

**Better Stack.** Free tier includes 10 monitors and heartbeats.

| Check | Target | Notes |
|---|---|---|
| Uptime, site | `https://app.<domain>/` | nginx should return 200 |
| Uptime, API | `POST https://app.<domain>/api/auth/login` with `{}` | **400** means the API is up. A 200 that is `index.html` means nginx is not proxying `/api` |
| Uptime, SignalR route | `POST https://app.<domain>/hubs/notifications/negotiate?negotiateVersion=1` with no token | **401** means the hub is reachable. 404 means `/hubs` is not proxied |
| Heartbeat, nightly dump | URL in `BETTERSTACK_HEARTBEAT_URL_BACKUP` | pinged only after upload |
| Heartbeat, pre-migration | `BETTERSTACK_HEARTBEAT_URL_PRE_MIGRATION` | do not reuse the nightly URL |
| Heartbeat, attachment mirror | optional | spare slot |
| Staging | same set on `staging.<domain>` | its own heartbeats |

Schedule the heartbeat for a few minutes after `cron.example` (18:15 UTC). If the ping does not arrive, Better Stack alerts. The script also requests `/fail` when the dump fails.

**Grafana and Sentry.** Create the free accounts when you want them. The API does not export OTLP and does not include the Sentry SDK, so `GRAFANA_OTLP_*` and `SENTRY_DSN` in `.env.example` are comments. Do not expect logs or errors to show up until that follow-up. `docker compose logs` on the VM is the beta debugger; json-file rotation is `10m x 3`.

## 6. First boot

Build the web image first (`WEB_PULL_POLICY=never` will not pull Docker Hub). Then:

```bash
cd /opt/worklane
deploy/beta/scripts/up.sh deploy/beta/.env.prod
```

Repeat with `.env.staging` after `WL63_DONE` exists. `up.sh` runs preflight, checks the two env files do not share secrets, checks `WL63_DONE`, then `docker compose up -d --build --wait`.

Confirm outbound TCP 443 works from a container on the project network (cloudflared, Resend, and the first `dotnet tool install` all need it). On a normal Docker install, custom bridges are masqueraded. If this fails, fix `ip_forward` and the iptables/nftables forwarding for Docker before debugging the tunnel:

```bash
docker run --rm --network worklane-prod-net postgres:16 \
  bash -c 'timeout 8 bash -c "echo >/dev/tcp/1.1.1.1/443" && echo ok'
```

Confirm there is no host port for Postgres:

```bash
docker compose --env-file deploy/beta/.env.prod -f deploy/beta/docker-compose.yml ps
ss -ltnp | grep 5432 || true   # nothing from this stack
```

Volumes are namespaced by project (`worklane-prod_pgdata`, `worklane-staging_pgdata`). Never set a fixed volume name.

## 7. Roles and migrations

The API applies EF migrations only when `ASPNETCORE_ENVIRONMENT=Development`, and that path also runs `DevSeeder`. Beta does not use Development.

Roles, created by `scripts/init-db-roles.sh`:

| Role | Login | Used for |
|---|---|---|
| `worklane_admin` (`POSTGRES_USER`) | yes, superuser | creating roles only |
| `worklane_owner` | no | group for ownership |
| `worklane_migrator` | yes | `dotnet ef database update` |
| `worklane_app` | yes | the API |
| `worklane_readonly` | yes | `pg_dump` |

`scripts/ef-migrate.sh .env.prod --yes` waits for Postgres, creates roles, writes a `pre-migration/` dump, runs `dotnet ef` inside the `migrator` profile (SDK image, not the running API), then grants again.

`dotnet ef` uses `WorklaneDbContextFactory`, which reads `ConnectionStrings__Default` when it is set. The migrator container sets that to the migrator role. With the variable unset, local `dotnet ef` still uses the dev connection string.

Run the same script for staging. Do not copy prod data into staging.

## 8. Smoke tests

Against `https://app.<domain>` after the tunnel is up.

1. `GET /` returns the SPA (200).
2. `POST /api/auth/login` with `{}` returns 400 and `application/problem+json`, not `index.html`.
3. Register a real beta user (password must be 8+ chars with upper, lower, and a digit). The response includes `accessToken`. Register also creates a personal workspace.
4. `GET /api/workspaces` with `Authorization: Bearer <accessToken>` returns that workspace. Create a project:

```bash
curl -sS -X POST "$ORIGIN/api/workspaces/$WORKSPACE_ID/projects" \
  -H "Authorization: Bearer $ACCESS_TOKEN" -H 'Content-Type: application/json' \
  -d '{"name":"Smoke","key":"SMK","description":null}'
```

5. Create a task (`priority` is `Low`, `Medium`, `High`, or `Urgent`):

```bash
curl -sS -X POST "$ORIGIN/api/projects/$PROJECT_ID/tasks" \
  -H "Authorization: Bearer $ACCESS_TOKEN" -H 'Content-Type: application/json' \
  -d '{"title":"smoke","description":null,"assigneeId":null,"priority":"Medium","dueDate":null,"columnId":null,"estimateDays":null}'
```

6. SignalR over the tunnel. Unauthenticated negotiate is 401:

```bash
curl -sS -o /dev/null -w '%{http_code}\n' -X POST \
  "$ORIGIN/hubs/notifications/negotiate?negotiateVersion=1"
```

With the bearer token, the same POST returns 200 and a body that contains `connectionToken`, and `availableTransports` includes WebSockets. Then open the board in a browser and confirm the network panel shows `wss://app.<domain>/hubs/notifications` with status 101, and that the socket stays up. Cloudflare's free plan proxies WebSockets. The tunnel is an HTTP reverse proxy and carries the upgrade.

7. About a 5 MB upload. The allow-list checks the declared content type. `text/plain` is allowed. The default cap is 10 MB.

```bash
dd if=/dev/zero bs=1M count=5 | tr '\0' 'a' > /tmp/smoke-5mb.txt
curl -sS -D- -o /tmp/upload.json -X POST \
  "$ORIGIN/api/tasks/$TASK_ID/attachments" \
  -H "Authorization: Bearer $ACCESS_TOKEN" \
  -F "file=@/tmp/smoke-5mb.txt;type=text/plain"
```

Expect 201. Download the attachment and check the size is 5242880 bytes. This path goes through the tunnel to local disk (`FileStorage__LocalPath`), not to R2.

8. Repeat a short version on staging (login 400, negotiate 401, one upload) so that project is known-good before a prod migration.

Delete the smoke user if it was not a real beta account. Do not invite the ten users until a restore drill has succeeded (below).

## 9. Backups

`scripts/pg-dump-to-r2.sh <env-file> [daily|pre-migration]`

- Runs `pg_dump -Fc --no-owner --no-acl` as `worklane_readonly`.
- Checks the archive with `pg_restore -l`.
- Encrypts with age unless `BACKUP_ALLOW_UNENCRYPTED=1`.
- Uploads to `s3://<backup-bucket>/<prefix>/<project>/...`.
- On Sunday (UTC) also copies that object to `weekly/`. On the 1st also copies it to `monthly/`.
- Pings the Better Stack heartbeat only after those uploads succeed. A failure pings `/fail` when a heartbeat URL is set.
- `pre-migration` writes only under `pre-migration/` and uses the pre-migration heartbeat, so it does not satisfy the nightly check.

`cron.example` is 18:15 UTC (01:15 in Thailand). Install it only after one manual run has uploaded an object you can list.

WAL-G (continuous WAL plus a weekly base backup, RPO about 5 minutes) is a follow-up. It is the right way to hit the SA 7-day PITR target, and it is larger than this change. Until it exists, RPO is the last successful dump, which is up to about 24 hours, and RTO is "build a VM and restore", which is not 1 hour with a guarantee.

Attachment mirror, until B-18:

```bash
deploy/beta/scripts/sync-attachments-to-r2.sh deploy/beta/.env.prod push
```

This syncs the Docker volume to `s3://<attachments-bucket>/mirror/<project>/`. The API never reads that bucket. There is no `--delete`, so a bad local directory cannot wipe the mirror. Restoring files is `pull` onto the volume **before** starting the API.

## 10. Restore

Practice this once before inviting users. A weekly drill restores the latest dump into a throwaway Compose project or a temporary database, checks `__EFMigrationsHistory`, and deletes the throwaway. That automation is not in this PR; the script below is the manual procedure.

On a new VM (or the same VM, understanding you will overwrite):

1. Install Docker and `age`. Clone the repo. Restore `.env.prod` from the password manager (`chmod 600`).
2. Put the age private key at `BACKUP_AGE_IDENTITY_FILE`, mode 600. Remove it when you are done.
3. `docker compose --env-file deploy/beta/.env.prod -f deploy/beta/docker-compose.yml up -d --wait postgres`
4. `deploy/beta/scripts/init-db-roles.sh deploy/beta/.env.prod`
5. List the bucket and pick a key under `daily/`, `weekly/`, `monthly/`, or `pre-migration/`.
6. `deploy/beta/scripts/restore-from-r2.sh deploy/beta/.env.prod '<key>' --yes`
   - Stops `api` and `web`.
   - Decrypts `.age` objects.
   - Refuses to restore over an existing `__EFMigrationsHistory` unless you also pass `--clean`.
   - `pg_restore --no-owner --no-acl` as `worklane_migrator`.
   - Re-applies grants.
7. `sync-attachments-to-r2.sh .env.prod pull` if you use the mirror.
8. Reconcile: attachment rows whose files are missing, and files nothing references. The API will 404 a missing file. B-18 does not exist yet, so this is a manual SQL check against `TaskAttachments` versus the volume.
9. Re-apply any PDPA deletion that was executed after the dump was taken. A restore brings that data back.
10. `up.sh`, then the smoke tests. Compare the latest `__EFMigrationsHistory` row with the build you deployed.

The private key and the plaintext dump must not be left in `/tmp`. The script shreds the plaintext it creates. Confirm with `ls` anyway.

## 11. Oracle reclaim: move to a DigitalOcean droplet

Trigger: Oracle says it will reclaim the VM, you cannot create a replacement A1, or keeping the box patched is no longer worth it. Do not generate fake CPU, network, or memory load.

The same Compose file runs on amd64. Set `DOCKER_PLATFORM=linux/amd64`. CI builds that architecture and does not push it, so the droplet builds locally. Start the build while the Oracle VM is still serving.

Target from the paid plan: Droplet Basic 4 GiB / 2 vCPU in SGP1, Ubuntu. Prod only during the move. Staging can wait. Suggested limits on 4 GiB: postgres `1g`, api `1536m`, web `128m`, cloudflared `128m`, `POSTGRES_SHARED_BUFFERS=256MB`.

1. Create the droplet. SSH key only. `ufw` denies inbound except SSH from your IP. Do not open 80, 443, or 5432. Install Docker and `age`.
2. Clone this repo and Worklane-Web. Build both images for `linux/amd64` (`worklane-api:beta`, `worklane-web:beta`). Copy `.env.prod` from the password manager and set `DOCKER_PLATFORM=linux/amd64` plus the 4 GiB limits.
3. If the Oracle VM is still up, run `pg-dump-to-r2.sh` and `sync-attachments-to-r2.sh` one last time. Then `docker compose stop api web cloudflared` on Oracle so clients stop writing and the tunnel token is free.
4. On the droplet: start Postgres, `init-db-roles.sh`, `restore-from-r2.sh ... --yes`, `sync-attachments-to-r2.sh pull`, then `up.sh`.
5. The public hostname does not change. The droplet's cloudflared uses the **same** prod token. One live connector. Smoke-test SignalR and a download of an existing attachment.
6. Leave the Oracle instance stopped until the droplet has survived a real session. Delete it once you are sure, or Oracle will delete it for you. Keep the R2 buckets; they are not on Oracle.

This is the same Compose project and the same env file, so the cutover is a restore plus a tunnel move. It can fit in an hour if the images are already built on the droplet before you stop Oracle. A cold npm build of Worklane-Web plus a cold `dotnet publish` can take longer. Build first.

A managed Postgres with PITR is a separate, later move (the $15.15 DO database in the paid plan). This runbook only moves the VM.

## 12. When to start paying

From the beta plan. Prices were checked on 18 Oct 2026 and are USD before tax.

| Trigger | Move | Extra cost |
|---|---|---|
| More than about 25 users, or a paying customer | the full DigitalOcean plan | $60.95 / month |
| You need RTO of 1 hour, managed PITR, or a backup SLA | DO Managed Postgres 1 GiB first | $15.15 / month |
| Oracle reclaim warning, no A1 capacity, or patching the VM is too much | DO Droplet 4 GiB | $24 / month |
| Neon fallback database near 1 GB, or CU-hours over about 70% | Neon Launch, or leave Neon | $0.106 / CU-hour + $0.35 / GB-month |
| Fallback cold start over a minute, complained about more than twice a week | a paid Render instance, or Oracle / DO | not re-checked in that plan |
| Mail over about 70 / day or about 2,100 / month | Resend Pro | $20 / month (50k messages) |
| R2 over 10 GB-month | pay for the gigabytes | $0.015 / GB-month |
| Logs kept longer than 14 days, or more than one Sentry user | Grafana Pro / Sentry Team | from about $19 / $26 / month |

Also leave free tier if you need an SLA, a standby, or someone else to replace a dead node. This stack has none of those.

## Fallback if Singapore A1 cannot be created

Render Free (Singapore) plus Neon Free (Singapore) plus R2 is the documented fallback. It is not a Compose file here. Render Free sleeps after 15 minutes, blocks SMTP ports 25/465/587 (the current `SmtpEmailService` would not send), has no persistent disk (B-18 is mandatory there), and Neon Free history is 6 hours. Do not start that path by adapting this Compose file in a hurry.

## Follow-ups

| Item | Why it is not in this PR |
|---|---|
| WL-63 in the API | No `kid`, no startup reject of the burned secret, dev compose still has the old value on purpose. The gate above is operational. |
| B-18 R2 storage | `IFileStorage` is only `LocalFileStorage`. The sync script mirrors the volume. The app does not use it. |
| `/health` | No health endpoint. Compose probes TCP on the API and wget/curl on nginx. Better Stack uses login 400 and negotiate 401 until `/health` exists. |
| Forwarded headers | `UseForwardedHeaders` is not installed. `UseHttpsRedirection` does nothing while the API is HTTP-only inside the network. Keep `ASPNETCORE_URLS=http://+:8080`. |
| Email From address | `SmtpEmailService` uses the SMTP username as From. Resend's username is `resend`, so real mail will fail. OCI port 587 was not confirmed. |
| WAL-G PITR | Dump script only. RPO is the last dump, not 5 minutes. |
| Grafana Alloy, Sentry SDK | No exporter in the API. Env keys are comments. |
| Image registry | CI builds `linux/amd64` and `linux/arm64` and does not push. The droplet builds locally. |
| Web nginx | Worklane-Web is a separate repo. Confirm `/hubs` upgrade headers there. |
| Weekly restore automation | Script is manual. Drill once before users, then decide if cron is safe. |
| Render + Neon | Not wired. |
| R2 token scope and bucket versioning | Not confirmed. Bucket lock is the control to turn on by hand. |

## Validate without deploying

```bash
deploy/beta/scripts/check-compose.sh
docker buildx build --platform linux/arm64,linux/amd64 \
  -f src/Worklane.WebApi/Dockerfile --pull=false .
```

`check-compose.sh` renders both project names, asserts nothing publishes a port, asserts memory limits and log rotation, rejects the burned JWT without printing it, and checks `up.sh` stops on the WL-63 file. It does not create accounts or start the stack.

The API image publishes on the builder's native architecture (`BUILDPLATFORM`) because the output is framework-dependent IL. The runtime image is the target architecture. That is what keeps the arm64 CI build from emulating the SDK.
