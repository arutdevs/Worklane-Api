#!/usr/bin/env bash
# Fail when a deploy compose project renders a dev S3 store or a variable image.
# Repo-root compose.dev.yml is the only file allowed to carry the local RustFS stack.
# compose.local.yml is local-only as well. Neither file is a deploy compose input.
# Files under deploy/ and files under .github/workflows must not reference them.
# The root .env.example carries dev values (loopback origin, WORKLANE_* ports, local
# S3 keys) and is not a deploy input. Fixture .env.example files do not carry those
# markers and are still passed through.
# Files under .github/guard-fixtures are negative tests and are not scanned here.
set -euo pipefail

docker compose version

# A variable that expands to the dev store still fails the rendered-token grep.
# The image check below does not depend on this: any "$" in services.*.image fails.
export S3_IMAGE='rustfs/rustfs:bypass@sha256:9ca95595aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'

# Jwt__Secret is ${JWT_SIGNING_KEY:?}. .env.example leaves that placeholder empty.
# A render-only value lets config succeed; it is not a signing key and is not deployed.
if [ -z "${JWT_SIGNING_KEY:-}" ]; then
  export JWT_SIGNING_KEY='render-only'
fi
# docker-compose.yml requires FRONTEND_BASE_URL. The dev example's loopback value
# is not a deploy input. This placeholder only lets config render.
if [ -z "${FRONTEND_BASE_URL:-}" ]; then
  export FRONTEND_BASE_URL='https://render-only.invalid'
fi

forbidden_re='rustfs|seaweedfs|9ca95595|ab024bfe|4e61d15f|http://s3:9000|RequireHttps.*false'
fail=0

check_variable_images() {
  local label="$1"
  shift
  local json hits
  if ! json="$(docker compose "$@" config --no-interpolate --format json)"; then
    echo "::error file=${label}::docker compose --profile '*' config --no-interpolate --format json failed"
    fail=1
    return
  fi
  if ! hits="$(printf '%s' "$json" | python3 -c '
import json, sys
doc = json.load(sys.stdin)
services = doc.get("services") or {}
if not isinstance(services, dict):
    raise SystemExit(0)
for name, svc in services.items():
    if not isinstance(svc, dict):
        continue
    image = svc.get("image")
    if isinstance(image, str) and "$" in image:
        print("%s image=%s" % (name, image))
')"; then
    echo "::error file=${label}::could not read compose config json"
    fail=1
    return
  fi
  if [ -n "$hits" ]; then
    printf '%s\n' "$hits"
    echo "::error file=${label}::Deploy compose service image contains a dollar sign."
    fail=1
  fi
}

render_config() {
  local label="$1"
  shift
  local rendered hits
  if ! rendered="$(docker compose "$@" config)"; then
    echo "::error file=${label}::docker compose config failed"
    fail=1
  else
    hits="$(printf '%s\n' "$rendered" | grep -E -i -n "$forbidden_re" || true)"
    if [ -n "$hits" ]; then
      printf '%s\n' "$hits"
      echo "::error file=${label}::Rendered compose contains a dev S3 image, digest, endpoint, or RequireHttps false."
      fail=1
    fi
  fi
  check_variable_images "$label" "$@"
}

# Root .env.example belongs to ./run-local.sh. A deploy render must not take it.
# Match markers, not the filename: fixture directories have their own .env.example.
is_local_dev_env_example() {
  local f="$1"
  grep -q -E \
    -e 'compose\.local\.ya?ml' \
    -e 'run-local\.sh' \
    -e '^WORKLANE_[A-Z0-9_]+=' \
    -e '^S3_ROOT_(USER|PASSWORD)=' \
    -e '^WL_S3_[A-Z0-9_]+=' \
    -e '^FRONTEND_BASE_URL=https?://(127\.0\.0\.1|localhost)([:/[:space:]]|$)' \
    -- "$f"
}

# Prints the env file when it exists and is not the local dev example.
usable_env_file() {
  local env_file="$1"
  if [ -f "$env_file" ] && ! is_local_dev_env_example "$env_file"; then
    printf '%s\n' "$env_file"
  fi
}

# --profile '*' keeps services that config would otherwise drop.
# --env-file is the sibling .env.example when that file exists and it is not the
# local dev example. A fixture .env.example still renders COMPOSE_PROJECT_NAME.
render_file() {
  local file="$1"
  local selected
  local -a args=()
  selected="$(usable_env_file "$(dirname "$file")/.env.example")"
  if [ -n "$selected" ]; then
    args+=(--env-file "$selected")
  fi
  args+=(-f "$file" --profile '*')
  render_config "$file" "${args[@]}"
}

# No -f: Compose merges the root file with an override the way a server does.
root_args=()
root_env="$(usable_env_file .env.example)"
if [ -n "$root_env" ]; then
  root_args+=(--env-file "$root_env")
fi
root_args+=(--profile '*')
render_config "(repo root compose project)" "${root_args[@]}"

# Filename match only. A deploy file named prod.yml is not selected; C7 in WL-71
# is the control for that gap. .github/guard-fixtures is excluded on purpose.
while IFS= read -r -d '' file; do
  rel="${file#./}"
  case "$rel" in
    compose.dev.yml|compose.dev.yaml|compose.local.yml|compose.local.yaml) continue ;;
  esac
  if grep -E -n '^[[:space:]]*(include|extends):' "$rel"; then
    echo "::error file=${rel}::Non-dev compose file must not use include: or extends:."
    fail=1
  fi
  case "$rel" in
    docker-compose.override.yml|docker-compose.override.yaml|compose.override.yml|compose.override.yaml)
      ;;
    *)
      render_file "$rel"
      ;;
  esac
done < <(find . \( -path './.git' -o -path './.github/guard-fixtures' \) -prune -o \
  -type f \( -name '*compose*.yml' -o -name '*compose*.yaml' \
             -o -name '*stack*.yml' -o -name '*stack*.yaml' \) -print0)

# Local-only names. compose.dev.yml may exist at the repo root. A copy or a
# mention under deploy/ or in a workflow is a deploy path using a local file.
local_compose_names=(compose.local.yml compose.local.yaml compose.dev.yml compose.dev.yaml)

load_dev_markers() {
  dev_markers=()
  local env_file="$1"
  [ -f "$env_file" ] || return 0
  if ! is_local_dev_env_example "$env_file"; then
    return 0
  fi
  local line key value
  while IFS= read -r line || [ -n "$line" ]; do
    line="${line//$'\r'/}"
    case "$line" in
      ''|\#*) continue ;;
    esac
    case "$line" in
      *=*) ;;
      *) continue ;;
    esac
    key="${line%%=*}"
    value="${line#*=}"
    key="${key#"${key%%[![:space:]]*}"}"
    key="${key%"${key##*[![:space:]]}"}"
    value="${value#"${value%%[![:space:]]*}"}"
    value="${value%"${value##*[![:space:]]}"}"
    case "$key" in
      WORKLANE_*|S3_ROOT_USER|S3_ROOT_PASSWORD|WL_S3_*)
        dev_markers+=("$key")
        ;;
    esac
    case "$value" in
      http://127.0.0.1:*|http://localhost:*|https://127.0.0.1:*|https://localhost:*)
        [ -n "$value" ] && dev_markers+=("$value")
        ;;
    esac
  done < "$env_file"
}

file_references_local_compose() {
  local file="$1"
  local name
  for name in "${local_compose_names[@]}"; do
    if grep -q -F -e "$name" -- "$file"; then
      return 0
    fi
  done
  return 1
}

# deploy/** and every workflow. Also a file whose name is one of the local compose files.
scan_deploy_paths() {
  local root file rel base
  for root in ./deploy ./.github/workflows; do
    [ -d "$root" ] || continue
    while IFS= read -r -d '' file; do
      rel="${file#./}"
      base="$(basename "$file")"
      case "$base" in
        compose.local.yml|compose.local.yaml|compose.dev.yml|compose.dev.yaml)
          echo "::error file=${rel}::Deploy path references compose.local.yml or compose.dev.yml."
          fail=1
          continue
          ;;
        run-local.sh)
          echo "::error file=${rel}::Deploy path references run-local.sh."
          fail=1
          continue
          ;;
      esac
      if file_references_local_compose "$file"; then
        echo "::error file=${rel}::Deploy path references compose.local.yml or compose.dev.yml."
        fail=1
      fi
      if grep -q -F -e 'run-local.sh' -- "$file"; then
        echo "::error file=${rel}::Deploy path references run-local.sh."
        fail=1
      fi
      if [ "${#dev_markers[@]}" -gt 0 ]; then
        local marker
        for marker in "${dev_markers[@]}"; do
          if grep -q -F -e "$marker" -- "$file"; then
            echo "::error file=${rel}::Deploy path references a .env.example dev value (${marker})."
            fail=1
          fi
        done
      fi
    done < <(find "$root" -type f -print0)
  done
}

# Dockerfiles outside the guard fixtures. COPY/ADD of a local-only file fails
# even when the Dockerfile itself is not under deploy/.
scan_dockerfiles() {
  local file rel line
  while IFS= read -r -d '' file; do
    rel="${file#./}"
    while IFS= read -r line || [ -n "$line" ]; do
      line="${line//$'\r'/}"
      case "$line" in
        \#*|[[:space:]]\#*) continue ;;
      esac
      case "$line" in
        COPY*|ADD*|[[:space:]]COPY*|[[:space:]]ADD*) ;;
        *) continue ;;
      esac
      if printf '%s\n' "$line" | grep -q -E '(^|[[:space:]"'"'"'])(\./)?(compose\.local\.ya?ml|compose\.dev\.ya?ml|run-local\.sh|\.env\.example)([[:space:]"'"'"']|$)'; then
        echo "::error file=${rel}::Dockerfile COPY or ADD references a local-only file."
        fail=1
      fi
    done < "$file"
  done < <(find . \( -path './.git' -o -path './.github/guard-fixtures' \) -prune -o \
    -type f \( -name 'Dockerfile' -o -name 'Dockerfile.*' -o -name '*.Dockerfile' \) -print0)
}

load_dev_markers .env.example
scan_deploy_paths
scan_dockerfiles

exit "$fail"
