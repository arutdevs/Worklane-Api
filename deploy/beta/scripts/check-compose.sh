#!/usr/bin/env bash
# Render the beta compose file with throwaway secrets and assert the safety properties.
# Also proves preflight rejects the leaked dev JWT without printing it.
set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
# shellcheck source=lib.sh
source "$SCRIPT_DIR/lib.sh"
cd "$(repo_root)"

compose_path=$(compose_file)
example="$(deploy_dir)/.env.example"
[[ -f "$example" ]] || die "missing $example"

mapfile -t referenced < <(grep -oE '\$\{[A-Z0-9_]+' "$compose_path" | sed 's/^\${//' | sort -u)
missing=0
for key in "${referenced[@]}"; do
  if ! grep -qE "^${key}=" "$example"; then
    printf 'error: %s is used in docker-compose.yml but missing from .env.example\n' "$key" >&2
    missing=1
  fi
done
[[ "$missing" -eq 0 ]] || exit 1

extra_keys=(
  POSTGRES_READONLY_USER
  POSTGRES_READONLY_PASSWORD
  R2_ACCOUNT_ID
  R2_BACKUP_BUCKET
  R2_BACKUP_ACCESS_KEY_ID
  R2_BACKUP_SECRET_ACCESS_KEY
  R2_ATTACHMENTS_BUCKET
  R2_ATTACHMENTS_ACCESS_KEY_ID
  R2_ATTACHMENTS_SECRET_ACCESS_KEY
  BETTERSTACK_HEARTBEAT_URL_BACKUP
  BETTERSTACK_HEARTBEAT_URL_PRE_MIGRATION
  BETTERSTACK_HEARTBEAT_URL_ATTACHMENTS
  BACKUP_AGE_RECIPIENT
  BACKUP_AGE_IDENTITY_FILE
  AWS_CLI_IMAGE
)
for key in "${extra_keys[@]}"; do
  if ! grep -qE "^${key}=" "$example"; then
    printf 'error: %s is missing from .env.example\n' "$key" >&2
    missing=1
  fi
done
[[ "$missing" -eq 0 ]] || exit 1

rand() {
  openssl rand -base64 48 | tr -d '\n'
}

umask 077
workdir=$(mktemp -d)
trap 'rm -rf "$workdir"' EXIT
envfile="$workdir/env"
cat >"$envfile" <<EOF
COMPOSE_PROJECT_NAME=worklane-prod
DOCKER_PLATFORM=linux/arm64
ASPNETCORE_ENVIRONMENT=Production
PUBLIC_ORIGIN=https://app.beta.test
ALLOWED_HOSTS=app.beta.test
POSTGRES_DB=worklane
POSTGRES_USER=worklane_admin
POSTGRES_PASSWORD=$(rand)
POSTGRES_APP_USER=worklane_app
POSTGRES_APP_PASSWORD=$(rand)
POSTGRES_MIGRATOR_USER=worklane_migrator
POSTGRES_MIGRATOR_PASSWORD=$(rand)
POSTGRES_READONLY_USER=worklane_readonly
POSTGRES_READONLY_PASSWORD=$(rand)
POSTGRES_SHARED_BUFFERS=512MB
POSTGRES_MAX_CONNECTIONS=40
POSTGRES_MEM_LIMIT=2g
POSTGRES_CPUS=0.60
API_IMAGE=worklane-api:beta
API_PULL_POLICY=never
API_MEM_LIMIT=1536m
API_CPUS=0.70
JWT_SECRET=$(openssl rand -base64 64 | tr -d '\n')
JWT_ISSUER=Worklane
JWT_AUDIENCE=WorklaneClient
FILE_MAX_BYTES=10485760
WEB_IMAGE=worklane-web:beta
WEB_PULL_POLICY=never
WEB_MEM_LIMIT=128m
WEB_CPUS=0.10
CLOUDFLARED_VERSION=2026.10.1
CLOUDFLARE_TUNNEL_TOKEN=$(rand)
CLOUDFLARED_MEM_LIMIT=128m
CLOUDFLARED_CPUS=0.10
SMTP_HOST=smtp.resend.com
SMTP_PORT=587
SMTP_USERNAME=resend
SMTP_PASSWORD=$(rand)
SMTP_FROM_NAME=Worklane
MIGRATOR_MEM_LIMIT=2g
MIGRATOR_CPUS=1.0
R2_ACCOUNT_ID=0123456789abcdef0123456789abcdef
R2_BACKUP_BUCKET=worklane-backup
R2_BACKUP_ACCESS_KEY_ID=$(rand)
R2_BACKUP_SECRET_ACCESS_KEY=$(rand)
AWS_CLI_IMAGE=amazon/aws-cli:2.38.1
BACKUP_AGE_RECIPIENT=age1placeholderxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx
BACKUP_AGE_IDENTITY_FILE=/root/worklane-backup-age.txt
BETTERSTACK_HEARTBEAT_URL_BACKUP=https://uptime.betterstack.com/api/v1/heartbeat/$(rand)
BETTERSTACK_HEARTBEAT_URL_PRE_MIGRATION=https://uptime.betterstack.com/api/v1/heartbeat/$(rand)
R2_ATTACHMENTS_BUCKET=worklane-attachments
R2_ATTACHMENTS_ACCESS_KEY_ID=$(rand)
R2_ATTACHMENTS_SECRET_ACCESS_KEY=$(rand)
BETTERSTACK_HEARTBEAT_URL_ATTACHMENTS=https://uptime.betterstack.com/api/v1/heartbeat/$(rand)
EOF
chmod 600 "$envfile"

"$SCRIPT_DIR/preflight.sh" --up "$envfile"
"$SCRIPT_DIR/preflight.sh" --backup "$envfile"

# Comments may name the forbidden values. Only non-comment lines count.
if grep -nE '^[[:space:]]*[^#[:space:]].*ASPNETCORE_ENVIRONMENT[[:space:]]*[:=][[:space:]]*["'\'']?Development' "$compose_path"; then
  die "beta compose sets ASPNETCORE_ENVIRONMENT=Development"
fi
if grep -nEi '^[[:space:]]*[^#[:space:]].*(rustfs|seaweedfs)' "$compose_path" .github/workflows/docker-image.yml; then
  die "beta deploy files must not reference rustfs or seaweedfs"
fi
if grep -n '^[[:space:]]*[^#[:space:]].*Jwt__Secret' "$compose_path" | grep -v '\${JWT_SECRET'; then
  die "Jwt__Secret in the beta compose must only reference \${JWT_SECRET}"
fi

rendered="$workdir/compose.json"
docker compose --env-file "$envfile" -f "$compose_path" config --format json >"$rendered"
docker compose --env-file "$envfile" -f "$compose_path" --profile migrate config --format json >"$workdir/migrate.json"

python3 - "$rendered" "$example" "$workdir/migrate.json" <<'PY'
import json, sys
path, example_path, migrate_path = sys.argv[1:]
doc = json.load(open(path))
def assert_no_public_ports(services):
    for name, service in services.items():
        ports = service.get("ports") or []
        for port in ports:
            host_ip = ""
            if isinstance(port, dict):
                host_ip = str(port.get("host_ip") or "")
            assert host_ip == "127.0.0.1", (name, port)
        if name in ("postgres", "api", "web", "cloudflared", "migrator"):
            assert ports == [], f"{name} publishes ports: {ports}"

assert doc["name"] == "worklane-prod", doc.get("name")
services = doc["services"]
assert "migrator" not in services, "migrator profile is part of a normal up"
assert_no_public_ports(services)
for name in ("postgres", "api", "web", "cloudflared"):
    service = services[name]
    env = service.get("environment") or {}
    assert env.get("ASPNETCORE_ENVIRONMENT", "Production") != "Development", name
    ports = service.get("ports") or []
    assert ports == [], f"{name} publishes ports: {ports}"
    assert service.get("platform") == "linux/arm64", name
    restart = service.get("restart")
    assert restart == "unless-stopped", (name, restart)
    assert "healthcheck" in service, name
    logging = service.get("logging") or {}
    options = logging.get("options") or {}
    assert options.get("max-size") == "10m", (name, logging)
    assert options.get("max-file") == "3", (name, logging)
    mem = service.get("mem_limit") or ((service.get("deploy") or {}).get("resources") or {}).get("limits", {}).get("memory")
    assert mem, f"{name} has no memory limit"
assert "5432:5432" not in open(path).read()
assert services["postgres"]["image"].startswith("postgres:16")
migrate = json.load(open(migrate_path))
assert_no_public_ports(migrate["services"])
assert migrate["services"]["migrator"]["restart"] == "no"
menv = migrate["services"]["migrator"]["environment"]
assert menv.get("ASPNETCORE_ENVIRONMENT") == "Production"
conn = menv["ConnectionStrings__Default"]
assert "Username=worklane_migrator" in conn
example = open(example_path).read()
assert "replace-me" in example
# The dev signing secret is base64 and must not be copied into the example or the render.
import re
dev = open("docker-compose.yml").read()
match = re.search(r'Jwt__Secret:\s*"([^"]+)"', dev)
assert match, "dev compose no longer has Jwt__Secret; update the denylist test"
secret = match.group(1)
blob = open(path).read() + "\n" + open(migrate_path).read() + "\n" + example
assert secret not in blob, "dev JWT leaked into the beta compose render or .env.example"
assert menv["Jwt__Secret"] != secret
api_env = services["api"]["environment"]
assert api_env["Jwt__Secret"] != secret
assert api_env["ASPNETCORE_ENVIRONMENT"] == "Production"
print("compose config ok")
PY

# up.sh must stop on the WL-63 gate before it talks to the Docker daemon.
set +e
up_out=$("$SCRIPT_DIR/up.sh" "$envfile" 2>&1)
up_status=$?
set -e
[[ "$up_status" -ne 0 ]]
printf '%s\n' "$up_out" | grep -q 'WL-63'
printf '%s\n' "$up_out" | grep -q 'preflight --up ok'

# Placeholder JWT is rejected.
bad="$workdir/bad"
sed 's/^JWT_SECRET=.*/JWT_SECRET=replace-me/' "$envfile" >"$bad"
set +e
"$SCRIPT_DIR/preflight.sh" --up "$bad" >"$workdir/bad.out" 2>&1
bad_status=$?
set -e
[[ "$bad_status" -ne 0 ]]
grep -q 'placeholder' "$workdir/bad.out"

# The committed dev secret is rejected, and the value is not echoed.
python3 - "$workdir/leaked-env" "$envfile" <<'PY'
import pathlib, re, sys
dest, src = sys.argv[1:]
dev = pathlib.Path("docker-compose.yml").read_text()
secret = re.search(r'Jwt__Secret:\s*"([^"]+)"', dev).group(1)
lines = []
for line in pathlib.Path(src).read_text().splitlines():
    if line.startswith("JWT_SECRET="):
        lines.append("JWT_SECRET=" + secret)
    else:
        lines.append(line)
pathlib.Path(dest).write_text("\n".join(lines) + "\n")
PY
chmod 600 "$workdir/leaked-env"
set +e
"$SCRIPT_DIR/preflight.sh" --up "$workdir/leaked-env" >"$workdir/leaked.out" 2>&1
leaked_status=$?
set -e
[[ "$leaked_status" -ne 0 ]]
grep -q 'leaked dev signing secret' "$workdir/leaked.out"
python3 - "$workdir/leaked.out" <<'PY'
import pathlib, re, sys
dev = pathlib.Path("docker-compose.yml").read_text()
secret = re.search(r'Jwt__Secret:\s*"([^"]+)"', dev).group(1)
out = pathlib.Path(sys.argv[1]).read_text()
assert secret not in out, "preflight printed the dev JWT"
print("leaked jwt rejected")
PY

# Prod and staging must not share a JWT.
staging="$workdir/staging"
sed \
  -e 's/^COMPOSE_PROJECT_NAME=.*/COMPOSE_PROJECT_NAME=worklane-staging/' \
  -e 's/^ASPNETCORE_ENVIRONMENT=.*/ASPNETCORE_ENVIRONMENT=Staging/' \
  -e 's#^PUBLIC_ORIGIN=.*#PUBLIC_ORIGIN=https://staging.beta.test#' \
  -e 's/^ALLOWED_HOSTS=.*/ALLOWED_HOSTS=staging.beta.test/' \
  "$envfile" >"$staging"
# Same JWT on purpose.
set +e
"$SCRIPT_DIR/check-env-isolation.sh" "$envfile" "$staging" >"$workdir/iso.out" 2>&1
iso_status=$?
set -e
[[ "$iso_status" -ne 0 ]]
grep -q 'JWT_SECRET is identical' "$workdir/iso.out"

# Different secrets pass. Rotating only the JWT still leaves the database passwords shared.
python3 - "$staging" <<'PY'
import pathlib, subprocess, sys
path = pathlib.Path(sys.argv[1])
rotate = {
    "JWT_SECRET",
    "POSTGRES_PASSWORD",
    "POSTGRES_APP_PASSWORD",
    "POSTGRES_MIGRATOR_PASSWORD",
    "POSTGRES_READONLY_PASSWORD",
    "CLOUDFLARE_TUNNEL_TOKEN",
    "SMTP_PASSWORD",
    "R2_BACKUP_SECRET_ACCESS_KEY",
    "R2_ATTACHMENTS_SECRET_ACCESS_KEY",
}
lines = []
for line in path.read_text().splitlines():
    key = line.split("=", 1)[0]
    if key in rotate:
        if key == "JWT_SECRET":
            value = subprocess.check_output(["openssl", "rand", "-base64", "64"], text=True).replace("\n", "")
        else:
            value = subprocess.check_output(["openssl", "rand", "-base64", "48"], text=True).replace("\n", "")
        lines.append(f"{key}={value}")
    else:
        lines.append(line)
path.write_text("\n".join(lines) + "\n")
PY
"$SCRIPT_DIR/check-env-isolation.sh" "$envfile" "$staging"

# Staging project name renders as its own compose project.
docker compose --env-file "$staging" -f "$compose_path" config --format json >"$workdir/staging.json"
python3 - "$workdir/staging.json" <<'PY'
import json, sys
doc = json.load(open(sys.argv[1]))
assert doc["name"] == "worklane-staging", doc.get("name")
assert doc["networks"]["default"]["name"] == "worklane-staging-net"
for service in doc["services"].values():
    assert not service.get("ports"), service.get("image")
print("staging project ok")
PY

printf 'check-compose.sh ok\n'
