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
trap 'rm -rf "$workdir"; rm -f "$(deploy_dir)/WL63_DONE"' EXIT
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
API_MEM_LIMIT=1536m
API_CPUS=0.70
JWT_SECRET=$(openssl rand -base64 64 | tr -d '\n')
JWT_ISSUER=Worklane
JWT_AUDIENCE=WorklaneClient
FILE_MAX_BYTES=10485760
WEB_MEM_LIMIT=128m
WEB_CPUS=0.10
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

# Comments may name Development. Only a real assignment counts.
if grep -nE '^[[:space:]]*[^#[:space:]].*ASPNETCORE_ENVIRONMENT[[:space:]]*[:=][[:space:]]*["'\'']?Development' "$compose_path"; then
  die "beta compose sets ASPNETCORE_ENVIRONMENT=Development"
fi
# The word itself is rejected in this file, including comments. PR #5's compose
# guard treats it as a dev object-store image.
if grep -nEi 'rustfs|seaweedfs' "$compose_path"; then
  die "beta compose must not mention rustfs or seaweedfs"
fi
if grep -n '^[[:space:]]*[^#[:space:]].*Jwt__Secret' "$compose_path" | grep -v '\${JWT_SECRET'; then
  die "Jwt__Secret in the beta compose must only reference \${JWT_SECRET}"
fi
if grep -nE '^[[:space:]]*image:[[:space:]]*.*\$\{' "$compose_path"; then
  die "image references in the beta compose must be literals, not variables"
fi
if ! grep -q 'USER $APP_UID' src/Worklane.WebApi/Dockerfile; then
  die "API Dockerfile must set USER \$APP_UID"
fi
if ! grep -q '/var/worklane/attachments' src/Worklane.WebApi/Dockerfile; then
  die "API Dockerfile must prepare the attachments path"
fi
if grep -nE '^[[:space:]]*actions:[[:space:]]*write' .github/workflows/docker-image.yml; then
  die "docker-image.yml must not grant actions: write"
fi
if grep -nE 'uses:[[:space:]]+[^#[:space:]]+@(v[0-9]|main|master)' .github/workflows/docker-image.yml; then
  die "GitHub Actions must be pinned by commit SHA"
fi
if grep -nE 'dotnet tool install|dotnet ef ' deploy/beta/scripts/ef-migrate.sh; then
  die "ef-migrate.sh must not install dotnet-ef at runtime"
fi
if grep -nE '^[[:space:]]*[^#].*--connection' deploy/beta/migrate.sh; then
  die "migrate.sh must not pass --connection"
fi
if grep -n '/src' deploy/beta/migrate.sh; then
  die "migrate.sh must not refer to a /src mount"
fi
if grep -nE '(^|[^[:alnum:]-])--build([^[:alnum:]-]|$)' deploy/beta/scripts/up.sh; then
  die "up.sh must not build images"
fi
if ! grep -q 'up -d --no-build' deploy/beta/scripts/up.sh; then
  die "up.sh must start containers with --no-build"
fi
if ! grep -q 'build_migrator_image' deploy/beta/scripts/ef-migrate.sh; then
  die "ef-migrate.sh must build the migrator from the locked API commit"
fi
if grep -n 'run --rm --no-build' deploy/beta/scripts/ef-migrate.sh; then
  die "compose run has no --no-build flag on this Compose; do not pass --build"
fi
if grep -nE 'http://web:80([^0-9]|$)' deploy/beta/docker-compose.yml deploy/beta/README.md; then
  die "tunnel origin must be http://web:8080"
fi
if grep -n 'mounts the repo read-only' deploy/beta/README.md; then
  die "README still says the migrator mounts the repo"
fi

rendered="$workdir/compose.json"
docker compose --env-file "$envfile" -f "$compose_path" config --format json >"$rendered"
docker compose --env-file "$envfile" -f "$compose_path" --profile migrate --profile tools config --format json >"$workdir/migrate.json"

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
        if name in ("postgres", "api", "web", "cloudflared", "migrator", "awscli"):
            assert ports == [], f"{name} publishes ports: {ports}"

def network_names(service):
    nets = service.get("networks") or {}
    if isinstance(nets, dict):
        return set(nets)
    return set(nets)

def tmpfs_targets(service):
    found = []
    for item in service.get("tmpfs") or []:
        if isinstance(item, str):
            found.append(item.split(":", 1)[0])
        elif isinstance(item, dict):
            found.append(item.get("target") or "")
    return found

def assert_hardened(name, service):
    assert service.get("read_only") is True, name
    assert "no-new-privileges:true" in (service.get("security_opt") or []), name
    assert "ALL" in (service.get("cap_drop") or []), name
    assert service.get("pids_limit"), name
    assert "/tmp" in tmpfs_targets(service), (name, service.get("tmpfs"))

assert doc["name"] == "worklane-prod", doc.get("name")
nets = doc["networks"]
assert set(nets) == {"edge", "app", "db"}, set(nets)
assert nets["db"].get("internal") is True
assert nets["edge"]["name"] == "worklane-prod-edge"
assert nets["app"]["name"] == "worklane-prod-app"
assert nets["db"]["name"] == "worklane-prod-db"
services = doc["services"]
assert "migrator" not in services, "migrator profile is part of a normal up"
assert "awscli" not in services, "awscli profile is part of a normal up"
assert_no_public_ports(services)
assert network_names(services["cloudflared"]) == {"edge"}
assert network_names(services["web"]) == {"edge", "app"}
assert network_names(services["api"]) == {"app", "db"}
assert network_names(services["postgres"]) == {"db"}
for name in ("postgres", "api", "web", "cloudflared"):
    service = services[name]
    assert_hardened(name, service)
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
assert services["api"]["image"] == "worklane-api:beta"
assert services["web"]["image"] == "worklane-web:beta"
assert services["api"].get("pull_policy") == "never"
assert services["web"].get("pull_policy") == "never"
assert "@sha256:" in services["postgres"]["image"]
assert "@sha256:" in services["cloudflared"]["image"]
pg_caps = set(services["postgres"].get("cap_add") or [])
assert pg_caps == {"CHOWN", "SETUID", "SETGID", "FOWNER", "DAC_OVERRIDE"}, pg_caps
for name in ("api", "web", "cloudflared"):
    assert not services[name].get("cap_add"), name
assert services["postgres"]["image"].startswith("postgres:16@sha256:")
assert services["cloudflared"]["image"].startswith("cloudflare/cloudflared:2026.10.1@sha256:")
migrate = json.load(open(migrate_path))
assert_no_public_ports(migrate["services"])
migrator = migrate["services"]["migrator"]
assert_hardened("migrator", migrator)
assert network_names(migrator) == {"db"}
assert migrator["image"] == "worklane-migrator:beta"
assert migrator.get("pull_policy") == "never"
assert migrator["restart"] == "no"
assert not migrator.get("cap_add")
menv = migrator["environment"]
assert menv.get("ASPNETCORE_ENVIRONMENT") == "Production"
assert "Jwt__Secret" not in menv
conn = menv["ConnectionStrings__Default"]
assert "Username=worklane_migrator" in conn
mounts = migrator.get("volumes") or []
assert mounts == [], mounts
web_test = " ".join(services["web"]["healthcheck"]["test"])
assert "127.0.0.1:8080" in web_test, web_test
assert "127.0.0.1/" not in web_test.replace("127.0.0.1:8080", ""), web_test
aws = migrate["services"]["awscli"]
assert_hardened("awscli", aws)
assert aws["image"].startswith("amazon/aws-cli:2.38.1@sha256:")
assert aws["restart"] == "no"
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
api_env = services["api"]["environment"]
assert api_env["Jwt__Secret"] != secret
assert api_env["ASPNETCORE_ENVIRONMENT"] == "Production"
print("compose config ok")
PY

# Fail when a beta service bind-mounts the repo root or anything under deploy/beta.
# Named volumes are ignored. A bind of some other path is allowed.
assert_no_repo_bind() {
  python3 - "$1" "$(repo_root)" <<'PY'
import json, os, sys
doc = json.load(open(sys.argv[1]))
repo = os.path.realpath(sys.argv[2])
bad = []
for name, service in doc.get("services", {}).items():
    for mount in service.get("volumes") or []:
        if not isinstance(mount, dict) or mount.get("type") != "bind":
            continue
        source = mount.get("source") or ""
        norm = os.path.realpath(source)
        parts = norm.split(os.sep)
        contains_beta = any(
            parts[i] == "deploy" and parts[i + 1] == "beta"
            for i in range(len(parts) - 1)
        )
        if norm == repo or contains_beta:
            bad.append(f"{name}:{source}")
if bad:
    print("beta service bind-mounts the repo or deploy/beta: " + ", ".join(bad), file=sys.stderr)
    sys.exit(1)
PY
}

assert_no_repo_bind "$rendered"
assert_no_repo_bind "$workdir/migrate.json"

# Control: a named volume and a bind outside the repo are not flagged.
python3 - "$rendered" "$workdir/ok-binds.json" "$(repo_root)" <<'PY'
import json, sys
doc = json.load(open(sys.argv[1]))
doc["services"]["api"]["volumes"] = [
    {"type": "volume", "source": "worklane-prod_pgdata", "target": "/var/lib/postgresql/data"},
    {"type": "bind", "source": "/var/lib/worklane-outside", "target": "/data"},
    {"type": "bind", "source": sys.argv[3] + "/src", "target": "/just-src"},
]
json.dump(doc, open(sys.argv[2], "w"))
PY
assert_no_repo_bind "$workdir/ok-binds.json"

# Negative: repo root, and a path that contains deploy/beta.
for source in "$(repo_root)" "$(repo_root)/deploy/beta" "$(repo_root)/deploy/beta/scripts"; do
  python3 - "$rendered" "$workdir/bad-bind.json" "$source" <<'PY'
import json, sys
doc = json.load(open(sys.argv[1]))
doc["services"]["api"]["volumes"] = [
    {"type": "bind", "source": sys.argv[3], "target": "/src", "read_only": True}
]
json.dump(doc, open(sys.argv[2], "w"))
PY
  set +e
  bind_out=$(assert_no_repo_bind "$workdir/bad-bind.json" 2>&1)
  bind_status=$?
  set -e
  [[ "$bind_status" -ne 0 ]]
  printf '%s\n' "$bind_out" | grep -q 'bind-mounts the repo or deploy/beta'
done

# Same check against a real compose render, not only a mutated document.
bad_override="$workdir/bad-bind.yml"
cat >"$bad_override" <<EOF
services:
  migrator:
    volumes:
      - type: bind
        source: $(repo_root)
        target: /src
        read_only: true
EOF
docker compose --env-file "$envfile" -f "$compose_path" -f "$bad_override" --profile migrate config --format json >"$workdir/bad-render.json"
set +e
bind_out=$(assert_no_repo_bind "$workdir/bad-render.json" 2>&1)
bind_status=$?
set -e
[[ "$bind_status" -ne 0 ]]
printf '%s\n' "$bind_out" | grep -q 'migrator:'

# Generated override: literal name and images, no ${ in an image line.
sha40=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
websha=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
# shellcheck source=lib.sh
source "$SCRIPT_DIR/lib.sh"
write_override_file "$workdir/sample.compose.yml" worklane-prod \
  "worklane-api:prod-${sha40}" \
  "worklane-migrator:prod-${sha40}" \
  "worklane-web:prod-${websha}"
grep -q '^name: worklane-prod$' "$workdir/sample.compose.yml"
if grep -nE '^[[:space:]]*image:[[:space:]]*.*\$' "$workdir/sample.compose.yml"; then
  die "sample override has a variable image"
fi
set +e
(
  write_override_file "$workdir/bad-image.yml" worklane-prod 'worklane-api:beta' 'worklane-migrator:beta'
) >"$workdir/bad-image.out" 2>&1
bad_image_status=$?
set -e
[[ "$bad_image_status" -ne 0 ]]
grep -q '40 hex' "$workdir/bad-image.out"
docker compose -f "$workdir/sample.compose.yml" config --no-interpolate --format json >"$workdir/sample.json"
python3 - "$workdir/sample.json" <<'PY'
import json, sys
doc = json.load(open(sys.argv[1]))
assert doc["name"] == "worklane-prod"
for service in doc["services"].values():
    image = service["image"]
    assert "$" not in image, image
    assert not image.endswith(":beta"), image
print("override images are literal")
PY

# up.sh must stop on the WL-63 gate before it talks to the Docker daemon.
set +e
up_out=$("$SCRIPT_DIR/up.sh" "$envfile" 2>&1)
up_status=$?
set -e
[[ "$up_status" -ne 0 ]]
printf '%s\n' "$up_out" | grep -q 'WL-63'
printf '%s\n' "$up_out" | grep -q 'preflight --up ok'

# After the gate, a missing image lock still refuses to build or start.
printf 'yes\n' >"$(deploy_dir)/WL63_DONE"
set +e
lock_out=$("$SCRIPT_DIR/up.sh" "$envfile" 2>&1)
lock_status=$?
set -e
rm -f "$(deploy_dir)/WL63_DONE"
[[ "$lock_status" -ne 0 ]]
printf '%s\n' "$lock_out" | grep -q 'image lock'
printf '%s\n' "$lock_out" | grep -q 'preflight --up ok'

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

# A tab inside the leaked value is still that key. FromBase64String ignores it.
python3 - "$workdir/tab-env" "$envfile" <<'PY'
import pathlib, re, sys
dest, src = sys.argv[1:]
dev = pathlib.Path("docker-compose.yml").read_text()
secret = re.search(r'Jwt__Secret:\s*"([^"]+)"', dev).group(1)
tabbed = secret[:8] + "\t" + secret[8:]
lines = []
for line in pathlib.Path(src).read_text().splitlines():
    if line.startswith("JWT_SECRET="):
        lines.append("JWT_SECRET=" + tabbed)
    else:
        lines.append(line)
pathlib.Path(dest).write_text("\n".join(lines) + "\n")
PY
chmod 600 "$workdir/tab-env"
set +e
"$SCRIPT_DIR/preflight.sh" --up "$workdir/tab-env" >"$workdir/tab.out" 2>&1
tab_status=$?
set -e
[[ "$tab_status" -ne 0 ]]
grep -q 'leaked dev signing secret' "$workdir/tab.out"
python3 - "$workdir/tab.out" <<'PY'
import pathlib, re, sys
dev = pathlib.Path("docker-compose.yml").read_text()
secret = re.search(r'Jwt__Secret:\s*"([^"]+)"', dev).group(1)
out = pathlib.Path(sys.argv[1]).read_text()
assert secret not in out and "\t" not in out
print("tabbed jwt rejected")
PY

# Decoded-bytes hash is enough on its own. A denylist with only that hash
# still rejects the exact base64 text.
python3 - "$workdir/key-only.txt" "$workdir/key-env" "$envfile" <<'PY'
import hashlib, base64, pathlib, re, sys
denylist, dest, src = sys.argv[1:]
dev = pathlib.Path("docker-compose.yml").read_text()
secret = re.search(r'Jwt__Secret:\s*"([^"]+)"', dev).group(1)
raw = base64.b64decode(secret, validate=True)
pathlib.Path(denylist).write_text(hashlib.sha256(raw).hexdigest() + "\n")
text = pathlib.Path(src).read_text().splitlines()
out = []
for line in text:
    if line.startswith("JWT_SECRET="):
        out.append("JWT_SECRET=" + secret)
    else:
        out.append(line)
pathlib.Path(dest).write_text("\n".join(out) + "\n")
PY
# preflight always reads the committed denylist, which also has the text hash.
# Call the function with a file that contains only the decoded-bytes hash.
set +e
(
  # shellcheck source=lib.sh
  source "$SCRIPT_DIR/lib.sh"
  secret=$(python3 - <<'PY'
import pathlib, re
dev = pathlib.Path("docker-compose.yml").read_text()
print(re.search(r'Jwt__Secret:\s*"([^"]+)"', dev).group(1), end="")
PY
)
  reject_leaked_jwt JWT_SECRET "$secret" "$workdir/key-only.txt"
) >"$workdir/key.out" 2>&1
key_status=$?
set -e
[[ "$key_status" -ne 0 ]]
grep -q 'leaked dev signing secret' "$workdir/key.out"
python3 - "$workdir/key.out" <<'PY'
import pathlib, re, sys
dev = pathlib.Path("docker-compose.yml").read_text()
secret = re.search(r'Jwt__Secret:\s*"([^"]+)"', dev).group(1)
out = pathlib.Path(sys.argv[1]).read_text()
assert secret not in out
print("decoded key hash rejected")
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
assert doc["networks"]["edge"]["name"] == "worklane-staging-edge"
assert doc["networks"]["app"]["name"] == "worklane-staging-app"
assert doc["networks"]["db"]["name"] == "worklane-staging-db"
assert doc["networks"]["db"].get("internal") is True
for service in doc["services"].values():
    assert not service.get("ports"), service.get("image")
print("staging project ok")
PY

# Same render PR #5's image-scan uses: sibling .env.example and every profile.
docker compose --env-file "$(deploy_dir)/.env.example" -f "$compose_path" --profile '*' config >/dev/null
printf 'check-compose.sh ok\n'
