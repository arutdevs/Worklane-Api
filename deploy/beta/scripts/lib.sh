# Shared helpers for the beta scripts. Sourced, not executed.
# Callers set -euo pipefail themselves.

deploy_dir() {
  cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd
}

repo_root() {
  cd "$(deploy_dir)/../.." && pwd
}

compose_file() {
  printf '%s/docker-compose.yml' "$(deploy_dir)"
}

die() {
  printf 'error: %s\n' "$1" >&2
  exit 1
}

# ENV_FILE is an absolute path set by the caller.
read_env() {
  local key=$1 line val
  [[ -n "${ENV_FILE:-}" && -f "$ENV_FILE" ]] || die "ENV_FILE is not set to a file"
  line=$(grep -E "^${key}=" "$ENV_FILE" | tail -n 1 || true)
  [[ -n "$line" ]] || return 1
  val=${line#*=}
  val=${val%$'\r'}
  if [[ "$val" == \"*\" && "$val" == *\" ]]; then
    val=${val#\"}
    val=${val%\"}
  elif [[ "$val" == \'*\' && "$val" == *\' ]]; then
    val=${val#\'}
    val=${val%\'}
  fi
  printf '%s' "$val"
}

require_env() {
  local key=$1 val
  if ! val=$(read_env "$key"); then
    die "$key is missing from $ENV_FILE"
  fi
  [[ -n "$val" ]] || die "$key is empty in $ENV_FILE"
  printf '%s' "$val"
}

optional_env() {
  local key=$1
  read_env "$key" 2>/dev/null || true
}

is_placeholder() {
  local v
  v=$(printf '%s' "$1" | tr '[:upper:]' '[:lower:]')
  case "$v" in
    ""|replace-me|changeme|change-me|change_me|password|worklane|postgres|secret|your-key|your-token|your-domain)
      return 0
      ;;
  esac
  [[ "$v" == *example.com* || "$v" == *example.invalid* ]] && return 0
  return 1
}

reject_placeholder() {
  local key=$1 val=$2
  if is_placeholder "$val"; then
    die "$key is still a placeholder"
  fi
}

# Identifiers interpolated into SQL. Passwords are escaped separately.
require_ident() {
  local key=$1 val
  val=$(require_env "$key")
  [[ "$val" =~ ^[a-z_][a-z0-9_]*$ ]] || die "$key must match [a-z_][a-z0-9_]* (got a value that is not a safe SQL identifier)"
  printf '%s' "$val"
}

reject_unsafe_secret() {
  local key=$1 val=$2
  # Whitespace includes tab. A tab is not a different secret: .NET's
  # FromBase64String ignores it, and the SQL/env builders do not want it either.
  if [[ "$val" =~ [[:space:]] ]]; then
    die "$key must not contain whitespace"
  fi
  # '$' and backticks would be expanded by the shell while building SQL or env files.
  if [[ "$val" == *"'"* || "$val" == *";"* || "$val" == *'$'* || "$val" == *'`'* || "$val" == *'\'* ]]; then
    die "$key must not contain quotes, dollar signs, backticks, backslashes, or semicolons"
  fi
}

# First field of each non-comment line in the denylist file.
denylist_has() {
  local file=$1 hash=$2
  grep -vE '^[[:space:]]*(#|$)' "$file" | awk 'NF {print $1}' | grep -qx "$hash"
}

# Reject the burned dev JWT. Compares SHA-256 of the base64 text and SHA-256 of
# the decoded key bytes. Whitespace is stripped only for that comparison, because
# FromBase64String ignores it; the value is still rejected if it contained any.
reject_leaked_jwt() {
  local key=$1 val=$2 denylist=$3
  local comparable text_hash raw decoded_hash len
  [[ -f "$denylist" ]] || die "missing $denylist"
  if [[ "$val" == *"'"* || "$val" == *";"* || "$val" == *'$'* || "$val" == *'`'* || "$val" == *'\'* ]]; then
    die "$key must not contain quotes, dollar signs, backticks, backslashes, or semicolons"
  fi
  comparable=$(printf '%s' "$val" | tr -d '[:space:]')
  text_hash=$(sha256_text "$comparable")
  raw=$(mktemp)
  if ! printf '%s' "$comparable" | base64 -d >"$raw" 2>/dev/null; then
    rm -f "$raw"
    if [[ "$val" =~ [[:space:]] ]]; then
      die "$key must not contain whitespace"
    fi
    die "$key is not valid base64"
  fi
  decoded_hash=$(sha256sum "$raw" | awk '{print $1}')
  len=$(wc -c <"$raw")
  rm -f "$raw"
  if denylist_has "$denylist" "$text_hash" || denylist_has "$denylist" "$decoded_hash"; then
    die "$key is the leaked dev signing secret (WL-63). Generate a new one. The old value stays burned."
  fi
  if [[ "$val" =~ [[:space:]] ]]; then
    die "$key must not contain whitespace"
  fi
  [[ "$len" -ge 32 ]] || die "$key must decode to at least 32 bytes (use: openssl rand -base64 64 | tr -d '\\n')"
}

# Literal pin from the awscli service. Backup scripts docker-run this image.
aws_cli_image() {
  local image
  image=$(awk '
    $1 == "awscli:" { in_svc = 1; next }
    in_svc && $0 ~ /^  [^[:space:]]/ { exit }
    in_svc && $1 == "image:" { print $2; exit }
  ' "$(compose_file)")
  [[ "$image" == amazon/aws-cli:*@sha256:* ]] || die "aws-cli image in deploy/beta/docker-compose.yml must be pinned as amazon/aws-cli:<tag>@sha256:<digest>"
  printf '%s' "$image"
}

sha256_text() {
  printf '%s' "$1" | sha256sum | awk '{print $1}'
}

compose() {
  local override
  override=$(image_override_file)
  if [[ -f "$override" ]]; then
    docker compose --env-file "$ENV_FILE" -f "$(compose_file)" -f "$override" "$@"
  else
    docker compose --env-file "$ENV_FILE" -f "$(compose_file)" "$@"
  fi
}

write_aws_env() {
  local dest=$1 key=$2 secret=$3
  umask 077
  printf 'AWS_ACCESS_KEY_ID=%s\nAWS_SECRET_ACCESS_KEY=%s\nAWS_DEFAULT_REGION=auto\n' "$key" "$secret" >"$dest"
  chmod 600 "$dest"
}

# amazon/aws-cli entrypoint is already `aws`. Extra args are the aws subcommand.
r2() {
  local awsenv=$1 workdir=$2 image=$3 endpoint=$4
  shift 4
  docker run --rm --env-file "$awsenv" -v "$workdir:/work" "$image" "$@" --endpoint-url "$endpoint"
}

# Absolute path. Rejects a missing file.
# Operator copies live in /etc/worklane and must be root-owned mode 600.
# .env.example stays in the repo; scripts still accept any path so tests can.
resolve_env_file() {
  local path=$1
  [[ -n "$path" && -f "$path" ]] || die "env file not found: ${path:-<missing>}"
  local dir base resolved mode owner
  dir=$(cd "$(dirname "$path")" && pwd)
  base=$(basename "$path")
  resolved="$dir/$base"
  case "$resolved" in
    /etc/worklane/*)
      mode=$(stat -c '%a' "$resolved")
      owner=$(stat -c '%U:%G' "$resolved")
      [[ "$mode" == "600" ]] || die "$resolved must be mode 600 (got $mode)"
      [[ "$owner" == "root:root" ]] || die "$resolved must be owned by root:root (got $owner)"
      ;;
  esac
  printf '%s' "$resolved"
}

# prod or staging. The filename wins. Otherwise COMPOSE_PROJECT_NAME.
env_slot() {
  local base
  base=$(basename "$ENV_FILE")
  case "$base" in
    prod.env|.env.prod) printf 'prod' ;;
    staging.env|.env.staging) printf 'staging' ;;
    *)
      case "$(require_env COMPOSE_PROJECT_NAME)" in
        worklane-prod) printf 'prod' ;;
        worklane-staging) printf 'staging' ;;
        *) die "cannot derive an env slot from $base" ;;
      esac
      ;;
  esac
}

# Sibling in the same directory, for either naming scheme. Empty if unknown.
sibling_env_file() {
  local base dir
  base=$(basename "$ENV_FILE")
  dir=$(dirname "$ENV_FILE")
  case "$base" in
    prod.env) printf '%s/staging.env' "$dir" ;;
    staging.env) printf '%s/prod.env' "$dir" ;;
    .env.prod) printf '%s/.env.staging' "$dir" ;;
    .env.staging) printf '%s/.env.prod' "$dir" ;;
  esac
}

# Lock and override sit next to the env file, outside the git checkout.
image_lock_file() {
  printf '%s/%s.images.lock' "$(dirname "$ENV_FILE")" "$(env_slot)"
}

image_override_file() {
  printf '%s/%s.compose.yml' "$(dirname "$ENV_FILE")" "$(env_slot)"
}

read_lock() {
  local key=$1 file line val
  file=$(image_lock_file)
  [[ -f "$file" ]] || die "image lock not found: $file. Run build-images.sh, then record-web-image.sh."
  line=$(grep -E "^${key}=" "$file" | tail -n 1 || true)
  [[ -n "$line" ]] || return 1
  val=${line#*=}
  val=${val%$'\r'}
  printf '%s' "$val"
}

require_lock() {
  local key=$1 val
  if ! val=$(read_lock "$key"); then
    die "$key is missing from $(image_lock_file)"
  fi
  [[ -n "$val" ]] || die "$key is empty in $(image_lock_file)"
  printf '%s' "$val"
}

# Update or append one key. The file is parsed by key, never sourced.
lock_set() {
  local key=$1 val=$2 file tmp
  [[ "$key" =~ ^[A-Z0-9_]+$ ]] || die "refusing lock key $key"
  [[ "$val" != *$'\n'* && "$val" != *'$'* ]] || die "refusing unsafe lock value for $key"
  file=$(image_lock_file)
  umask 077
  tmp=$(mktemp)
  if [[ -f "$file" ]]; then
    grep -vE "^${key}=" "$file" >"$tmp" || true
  fi
  printf '%s=%s\n' "$key" "$val" >>"$tmp"
  mv "$tmp" "$file"
  chmod 600 "$file"
}

git_head() {
  git -C "$(repo_root)" rev-parse HEAD
}

require_full_sha() {
  local label=$1 sha=$2
  [[ "$sha" =~ ^[0-9a-f]{40}$ ]] || die "$label must be a 40-character hex commit SHA"
}

image_id_of() {
  local image=$1 id
  id=$(docker image inspect --format '{{.Id}}' "$image" 2>/dev/null || true)
  [[ "$id" == sha256:* ]] || die "local image $image is missing. Tag it, then record docker image inspect --format '{{.Id}}'."
  printf '%s' "$id"
}

# worklane-<kind>:<slot>-<40 hex>. The shared :beta tag is refused.
assert_slot_image() {
  local kind=$1 image=$2 slot=$3
  [[ "$image" =~ ^worklane-${kind}:${slot}-[0-9a-f]{40}$ ]] || die "$kind image must be worklane-${kind}:${slot}-<40 hex sha>"
  [[ "$image" != *'$'* && "$image" != *'${'* ]] || die "$kind image contains a variable"
}

verify_locked_image() {
  local kind=$1 image id actual
  image=$(require_lock "${kind}_IMAGE")
  id=$(require_lock "${kind}_IMAGE_ID")
  [[ "$id" == sha256:* ]] || die "${kind}_IMAGE_ID is not a docker image id"
  actual=$(image_id_of "$image")
  [[ "$actual" == "$id" ]] || die "$image id is $actual but the lock has $id. Rebuild and record it again."
}

# Literal name and image lines only. web may be empty when the caller has not
# recorded the Worklane-Web image yet (init-db, a migrator rebuild).
write_override_file() {
  local dest=$1 project=$2 api=$3 migrator=$4 web=${5:-}
  local slot
  case "$project" in
    worklane-prod) slot=prod ;;
    worklane-staging) slot=staging ;;
    *) die "override project must be worklane-prod or worklane-staging" ;;
  esac
  assert_slot_image api "$api" "$slot"
  assert_slot_image migrator "$migrator" "$slot"
  if [[ -n "$web" ]]; then
    assert_slot_image web "$web" "$slot"
  fi
  umask 077
  {
    printf '# Generated beside the env file. Literal images only. Do not commit.\n'
    printf 'name: %s\n' "$project"
    printf 'services:\n'
    printf '  api:\n'
    printf '    image: %s\n' "$api"
    printf '  migrator:\n'
    printf '    image: %s\n' "$migrator"
    if [[ -n "$web" ]]; then
      printf '  web:\n'
      printf '    image: %s\n' "$web"
    fi
  } >"$dest"
  chmod 600 "$dest"
  if grep -nE '^[[:space:]]*image:[[:space:]]*.*\$' "$dest"; then
    die "generated override has a variable in an image line"
  fi
}

write_image_override() {
  local require_web=${1:-1}
  local slot project api_sha api_image migrator_sha migrator_image web_sha web_image dest
  slot=$(env_slot)
  project=$(require_env COMPOSE_PROJECT_NAME)
  case "$slot:$project" in
    prod:worklane-prod|staging:worklane-staging) ;;
    *) die "env slot $slot does not match COMPOSE_PROJECT_NAME $project" ;;
  esac
  api_sha=$(require_lock API_COMMIT)
  require_full_sha API_COMMIT "$api_sha"
  api_image=$(require_lock API_IMAGE)
  migrator_sha=$(require_lock MIGRATOR_COMMIT)
  require_full_sha MIGRATOR_COMMIT "$migrator_sha"
  [[ "$migrator_sha" == "$api_sha" ]] || die "MIGRATOR_COMMIT does not match API_COMMIT"
  migrator_image=$(require_lock MIGRATOR_IMAGE)
  verify_locked_image API
  verify_locked_image MIGRATOR
  web_image=""
  if [[ "$require_web" == "1" ]]; then
    web_sha=$(require_lock WEB_COMMIT)
    require_full_sha WEB_COMMIT "$web_sha"
    web_image=$(require_lock WEB_IMAGE)
    verify_locked_image WEB
  else
    web_image=$(read_lock WEB_IMAGE || true)
    if [[ -n "$web_image" ]]; then
      web_sha=$(require_lock WEB_COMMIT)
      require_full_sha WEB_COMMIT "$web_sha"
      verify_locked_image WEB
    fi
  fi
  dest=$(image_override_file)
  write_override_file "$dest" "$project" "$api_image" "$migrator_image" "$web_image"
}

build_api_image() {
  local slot sha platform image id
  slot=$(env_slot)
  sha=$(git_head)
  require_full_sha HEAD "$sha"
  platform=$(require_env DOCKER_PLATFORM)
  image="worklane-api:${slot}-${sha}"
  docker build \
    --platform "$platform" \
    --tag "$image" \
    --file "$(repo_root)/src/Worklane.WebApi/Dockerfile" \
    "$(repo_root)"
  id=$(image_id_of "$image")
  lock_set API_COMMIT "$sha"
  lock_set API_IMAGE "$image"
  lock_set API_IMAGE_ID "$id"
  verify_locked_image API
}

# The migrator is always built from the same commit as the locked API image.
build_migrator_image() {
  local slot sha platform image id api_commit
  slot=$(env_slot)
  sha=$(git_head)
  require_full_sha HEAD "$sha"
  api_commit=$(require_lock API_COMMIT)
  [[ "$api_commit" == "$sha" ]] || die "HEAD is not the locked API commit. Run build-images.sh on that commit before migrating."
  platform=$(require_env DOCKER_PLATFORM)
  image="worklane-migrator:${slot}-${sha}"
  docker build \
    --platform "$platform" \
    --tag "$image" \
    --file "$(deploy_dir)/Dockerfile" \
    "$(repo_root)"
  id=$(image_id_of "$image")
  lock_set MIGRATOR_COMMIT "$sha"
  lock_set MIGRATOR_IMAGE "$image"
  lock_set MIGRATOR_IMAGE_ID "$id"
  verify_locked_image MIGRATOR
}

# Rendered compose must be running the locked per-commit tag, not :beta.
# kind is the lock prefix (API, WEB, MIGRATOR). The image name is lowercase.
assert_locked_service_image() {
  local json_file=$1 service=$2 kind=$3 expected image_kind
  expected=$(require_lock "${kind}_IMAGE")
  image_kind=$(printf '%s' "$kind" | tr '[:upper:]' '[:lower:]')
  python3 - "$json_file" "$service" "$image_kind" "$expected" <<'PY'
import json, re, sys
doc = json.load(open(sys.argv[1]))
service, kind, expected = sys.argv[2:]
image = doc["services"][service]["image"]
if "$" in image or image.endswith(":beta"):
    raise SystemExit(f"{service} image is not a literal per-commit tag")
if not re.fullmatch(rf"worklane-{kind}:(prod|staging)-[0-9a-f]{{40}}", image):
    raise SystemExit(f"{service} image is not a per-commit tag")
if image != expected:
    raise SystemExit(f"{service} image does not match the lock")
PY
}
