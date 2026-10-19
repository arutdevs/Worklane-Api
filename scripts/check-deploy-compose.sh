#!/usr/bin/env bash
# Fail when a deploy compose project renders a dev S3 store or a variable image.
# Repo-root compose.dev.yml is the only file allowed to carry the local RustFS stack.
# Files under .github/guard-fixtures are negative tests and are not scanned here.
set -euo pipefail

docker compose version

# A variable that expands to the dev store still fails the rendered-token grep.
# The image check below does not depend on this: any "$" in services.*.image fails.
export S3_IMAGE='rustfs/rustfs:bypass@sha256:9ca95595aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'

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

# --profile '*' keeps services that config would otherwise drop.
# --env-file is the sibling .env.example when that file exists, so a required
# variable such as COMPOSE_PROJECT_NAME still renders. The file is not skipped.
render_file() {
  local file="$1"
  local env_file
  local -a args=()
  env_file="$(dirname "$file")/.env.example"
  if [ -f "$env_file" ]; then
    args+=(--env-file "$env_file")
  fi
  args+=(-f "$file" --profile '*')
  render_config "$file" "${args[@]}"
}

# No -f: Compose merges the root file with an override the way a server does.
root_args=()
if [ -f .env.example ]; then
  root_args+=(--env-file .env.example)
fi
root_args+=(--profile '*')
render_config "(repo root compose project)" "${root_args[@]}"

# Filename match only. A deploy file named prod.yml is not selected; C7 in WL-71
# is the control for that gap. .github/guard-fixtures is excluded on purpose.
while IFS= read -r -d '' file; do
  rel="${file#./}"
  case "$rel" in
    compose.dev.yml|compose.dev.yaml) continue ;;
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

exit "$fail"
