#!/usr/bin/env bash
# Shared helpers for the prod backup scripts. Do not execute this file.
set -euo pipefail

die() {
  printf 'backup: %s\n' "$1" >&2
  exit 1
}

require_env() {
  local name=$1
  if [[ -z "${!name:-}" ]]; then
    die "${name} is empty"
  fi
}

reject_placeholders() {
  local name value
  for name in "$@"; do
    value=${!name-}
    if [[ -z "$value" || "$value" == "CHANGE_ME" || "$value" == *"CHANGE_ME"* ]]; then
      die "${name} is still a placeholder"
    fi
    case "$value" in
      *example.com*|*example.r2.cloudflarestorage.com*)
        die "${name} still points at example.com"
        ;;
    esac
  done
}

load_env_file() {
  local file=$1 line key value mode owner
  [[ -f "$file" && ! -L "$file" ]] || die "env file must be a regular file"
  mode=$(stat -c %a "$file")
  owner=$(stat -c %u "$file")
  if [[ "$mode" != "600" && "$mode" != "400" ]]; then
    die "env file mode must be 600 or 400"
  fi
  if [[ "$owner" != "0" && "$owner" != "$(id -u)" ]]; then
    die "env file must be owned by root or the current user"
  fi
  while IFS= read -r line || [[ -n "$line" ]]; do
    line=${line%$'\r'}
    [[ -z "$line" || "$line" == \#* ]] && continue
    [[ "$line" =~ ^[A-Za-z_][A-Za-z0-9_]*= ]] || die "env file has a line that is not KEY=value"
    key=${line%%=*}
    value=${line#*=}
    # shellcheck disable=SC2016
    case "$value" in
      *'$('*|*'`'*|*'${'*) die "env file values must not use substitution" ;;
    esac
    printf -v "$key" '%s' "$value"
    export "${key?}"
  done <"$file"
}

validate_object_key() {
  local key=$1
  [[ "$key" =~ ^(daily|pre-migration)/[A-Za-z0-9._/-]+$ ]] || die "object key must sit under daily/ or pre-migration/"
  [[ "$key" != *..* ]] || die "object key must not contain .."
  case "$key" in
    *.dump|*.dump.age|*.dump.gpg) ;;
    *) die "object key must end in .dump, .dump.age, or .dump.gpg" ;;
  esac
}

# Prints pg_dump bytes to stdout. Caller redirects.
run_pg_dump() {
  local -a dump_args
  dump_args=(-Fc -Z 6 --no-owner --no-acl -h 127.0.0.1 -p "${PGPORT:-5432}" -U "$PGUSER" -d "$PGDATABASE")
  if [[ -n "${WORKLANE_COMPOSE_FILE:-}" ]]; then
    compose exec -T \
      -e "PGPASSWORD=${PGPASSWORD}" \
      postgres \
      pg_dump "${dump_args[@]}"
  elif [[ -n "${BACKUP_DOCKER_CONTAINER:-}" ]]; then
    docker exec -e "PGPASSWORD=${PGPASSWORD}" "$BACKUP_DOCKER_CONTAINER" \
      pg_dump "${dump_args[@]}"
  else
    require_env PGHOST
    PGPASSWORD="$PGPASSWORD" pg_dump -Fc -Z 6 --no-owner --no-acl \
      -h "$PGHOST" -p "${PGPORT:-5432}" -U "$PGUSER" -d "$PGDATABASE"
  fi
}

compose() {
  local -a args
  args=(docker compose -f "$WORKLANE_COMPOSE_FILE")
  if [[ -n "${WORKLANE_COMPOSE_ENV:-}" ]]; then
    args+=(--env-file "$WORKLANE_COMPOSE_ENV")
  fi
  "${args[@]}" "$@"
}

rclone_env() {
  local access=$1 secret=$2
  export RCLONE_CONFIG_WL_TYPE=s3
  export RCLONE_CONFIG_WL_PROVIDER="${R2_PROVIDER:-Cloudflare}"
  export RCLONE_CONFIG_WL_ACCESS_KEY_ID="$access"
  export RCLONE_CONFIG_WL_SECRET_ACCESS_KEY="$secret"
  export RCLONE_CONFIG_WL_ENDPOINT="$R2_ENDPOINT"
  export RCLONE_CONFIG_WL_REGION="${R2_REGION:-auto}"
  export RCLONE_CONFIG_WL_FORCE_PATH_STYLE=true
  export RCLONE_CONFIG_WL_NO_CHECK_BUCKET=true
  if [[ "${R2_DISABLE_MULTIPART:-}" == "1" ]]; then
    export RCLONE_CONFIG_WL_USE_MULTIPART_UPLOADS=false
  fi
}

rclone_docker_env() {
  local -n _out=$1
  _out=(
    -e RCLONE_CONFIG_WL_TYPE
    -e RCLONE_CONFIG_WL_PROVIDER
    -e RCLONE_CONFIG_WL_ACCESS_KEY_ID
    -e RCLONE_CONFIG_WL_SECRET_ACCESS_KEY
    -e RCLONE_CONFIG_WL_ENDPOINT
    -e RCLONE_CONFIG_WL_REGION
    -e RCLONE_CONFIG_WL_FORCE_PATH_STYLE
    -e RCLONE_CONFIG_WL_NO_CHECK_BUCKET
  )
  if [[ -n "${RCLONE_CONFIG_WL_USE_MULTIPART_UPLOADS:-}" ]]; then
    _out+=(-e RCLONE_CONFIG_WL_USE_MULTIPART_UPLOADS)
  fi
}

# upload_object <local-file> <object-key> <access-key> <secret-key>
upload_object() {
  local src=$1 key=$2 access=$3 secret=$4
  local -a env_args
  rclone_env "$access" "$secret"
  if [[ -n "${BACKUP_UPLOAD_NETWORK:-}" ]]; then
    require_env BACKUP_UPLOAD_IMAGE
    rclone_docker_env env_args
    docker run --rm --network "$BACKUP_UPLOAD_NETWORK" \
      "${env_args[@]}" \
      -v "$src:/work/payload:ro" \
      "$BACKUP_UPLOAD_IMAGE" \
      -q copyto /work/payload "WL:${R2_BUCKET}/${key}"
    return 0
  fi
  command -v rclone >/dev/null 2>&1 || die "rclone is not installed and BACKUP_UPLOAD_NETWORK is empty"
  rclone -q copyto "$src" "WL:${R2_BUCKET}/${key}"
}

# download_object <object-key> <dest-file> <access-key> <secret-key>
download_object() {
  local key=$1 dest=$2 access=$3 secret=$4
  local -a env_args
  rclone_env "$access" "$secret"
  if [[ -n "${BACKUP_UPLOAD_NETWORK:-}" ]]; then
    require_env BACKUP_UPLOAD_IMAGE
    rclone_docker_env env_args
    docker run --rm --network "$BACKUP_UPLOAD_NETWORK" \
      "${env_args[@]}" \
      -v "$(dirname "$dest"):/work" \
      "$BACKUP_UPLOAD_IMAGE" \
      -q copyto "WL:${R2_BUCKET}/${key}" "/work/$(basename "$dest")"
    return 0
  fi
  command -v rclone >/dev/null 2>&1 || die "rclone is not installed and BACKUP_UPLOAD_NETWORK is empty"
  rclone -q copyto "WL:${R2_BUCKET}/${key}" "$dest"
}

ping_heartbeat() {
  local url=$1
  [[ -n "$url" ]] || return 0
  curl -fs -m 15 -o /dev/null -- "$url" || printf 'warning: heartbeat ping failed\n' >&2
}
