#!/usr/bin/env bash
# Build and start the local Worklane stack: API, Angular web, Postgres 16, migrator.
# With no arguments this is `up --build`. Any other arguments are passed to
# docker compose, so `./run-local.sh down` and `./run-local.sh down -v` work.
set -euo pipefail

# Reads compose config JSON on stdin. Exits 1 when a published port binds
# anything other than 127.0.0.1. WORKLANE_CHECK_CONFIG_JSON=1 runs only this
# check so a test can feed config JSON without Docker or the web checkout.
check_published_ports_json() {
  python3 -c "$(cat <<'PY'
import json
import sys

try:
    doc = json.load(sys.stdin)
except json.JSONDecodeError as exc:
    sys.stderr.write("docker compose config JSON could not be read: %s\n" % exc)
    sys.exit(1)

services = doc.get("services") or {}
bad = []
if isinstance(services, dict):
    for name, svc in services.items():
        if not isinstance(svc, dict):
            continue
        ports = svc.get("ports") or []
        if not isinstance(ports, list):
            continue
        for port in ports:
            if not isinstance(port, dict):
                continue
            if (port.get("mode") or "ingress") != "ingress":
                continue
            published = port.get("published")
            if published in (None, ""):
                continue
            host_ip = port.get("host_ip") or "0.0.0.0"
            if str(host_ip) != "127.0.0.1":
                bad.append("%s publishes %s on %s" % (name, published, host_ip))

if bad:
    sys.stderr.write("Refusing to start. Published ports must bind 127.0.0.1 only.\n")
    for line in bad:
        sys.stderr.write(line + "\n")
    sys.exit(1)
PY
)"
}

if [[ "${WORKLANE_CHECK_CONFIG_JSON:-}" == "1" ]]; then
  check_published_ports_json
  exit 0
fi

root=$(CDPATH= cd -- "$(dirname "$0")" && pwd)
cd "$root"

assert_loopback_ports() {
  local rendered
  if ! rendered="$(docker compose "${compose_args[@]}" config --format json)"; then
    echo "docker compose config failed, so published ports were not checked." >&2
    exit 1
  fi
  printf '%s' "$rendered" | check_published_ports_json
}

if ! command -v docker >/dev/null 2>&1 || ! docker compose version >/dev/null 2>&1; then
  echo "Docker Compose v2 is required. See RUN-LOCAL.md." >&2
  exit 1
fi

ensure_env() {
  if [[ ! -f .env ]]; then
    cp .env.example .env
  fi
  python3 - <<'PY'
import base64
import os
import pathlib
import secrets

path = pathlib.Path(".env")
text = path.read_text()
lines = text.splitlines()
has_jwt = any(line.startswith("JWT_SIGNING_KEY=") and line.split("=", 1)[1].strip() for line in lines)
out = list(lines)
changed = False
if not has_jwt:
    key = base64.b64encode(secrets.token_bytes(48)).decode()
    replaced = False
    rewritten = []
    for line in out:
        if line.startswith("JWT_SIGNING_KEY=") and not replaced:
            rewritten.append("JWT_SIGNING_KEY=" + key)
            replaced = True
        else:
            rewritten.append(line)
    if not replaced:
        rewritten.append("JWT_SIGNING_KEY=" + key)
    out = rewritten
    changed = True
if not any(line.startswith("FRONTEND_BASE_URL=") and line.split("=", 1)[1].strip() for line in out):
    out.append("FRONTEND_BASE_URL=http://127.0.0.1:4200")
    changed = True
if not changed:
    raise SystemExit(0)
path.write_text("\n".join(out) + "\n")
os.chmod(path, 0o600)
PY
}

resolve_web_dir() {
  if [[ -n "${WORKLANE_WEB_DIR:-}" ]]; then
    printf '%s\n' "$WORKLANE_WEB_DIR"
    return
  fi
  local from_env=""
  if [[ -f .env ]]; then
    from_env=$(python3 - <<'PY'
from pathlib import Path
for line in Path(".env").read_text().splitlines():
    if line.startswith("WORKLANE_WEB_DIR="):
        print(line.split("=", 1)[1].strip().strip('"').strip("'"))
        break
PY
)
  fi
  if [[ -n "$from_env" ]]; then
    printf '%s\n' "$from_env"
    return
  fi
  printf '%s\n' "$(CDPATH= cd -- "$root/.." && pwd)/Worklane-Web"
}

ensure_env
web_dir=$(resolve_web_dir)
if [[ "$web_dir" != /* ]]; then
  if [[ -d "$root/$web_dir" ]]; then
    web_dir=$(CDPATH= cd -- "$root/$web_dir" && pwd)
  else
    web_dir="$root/$web_dir"
  fi
fi
if [[ ! -f "$web_dir/Dockerfile" ]]; then
  echo "Worklane-Web Dockerfile not found at: $web_dir" >&2
  echo "Clone arutdevs/Worklane-Web next to this repo, or set WORKLANE_WEB_DIR. See RUN-LOCAL.md." >&2
  exit 1
fi
export WORKLANE_WEB_DIR="$web_dir"

compose_args=(-f docker-compose.yml)
if [[ -f "$web_dir/docker-compose.yml" ]]; then
  compose_args+=(-f "$web_dir/docker-compose.yml")
fi
compose_args+=(-f compose.local.yml)

if [[ $# -eq 0 ]]; then
  assert_loopback_ports
  docker compose "${compose_args[@]}" up --build --detach --wait
  web_port=$(python3 - <<'PY'
import os
from pathlib import Path
port = os.environ.get("WORKLANE_WEB_PORT", "").strip()
if not port:
    for name in (".env", ".env.example"):
        path = Path(name)
        if not path.exists():
            continue
        for line in path.read_text().splitlines():
            if line.startswith("WORKLANE_WEB_PORT=") and line.split("=", 1)[1].strip():
                port = line.split("=", 1)[1].strip()
                break
        if port:
            break
print(port or "4200")
PY
)
  printf 'Worklane is up at http://127.0.0.1:%s\n' "$web_port"
  printf 'Demo login: demo@worklane.app / Demo123!\n'
elif [[ "${1:-}" == "up" ]]; then
  assert_loopback_ports
  docker compose "${compose_args[@]}" "$@"
else
  docker compose "${compose_args[@]}" "$@"
fi
