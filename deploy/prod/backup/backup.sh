#!/usr/bin/env bash
# pg_dump -Fc (already compressed) -> optional age or gpg -> one PutObject to R2.
# Does not delete objects. Retention is the bucket lifecycle (30 days during beta).
# The daily token is write-only: this script does not GetObject or DeleteObject.
#
#   backup.sh [env-file] [daily|pre-migration]
#
# The env file, when given, must be mode 600 and owned by root or the current user.
# A successful run prints one line: BACKUP_OBJECT=<key>
set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
# shellcheck source=deploy/prod/backup/lib.sh
source "$SCRIPT_DIR/lib.sh"

mode=daily
if [[ $# -ge 1 ]]; then
  case "$1" in
    daily|pre-migration)
      mode=$1
      ;;
    *)
      [[ -f "$1" ]] || die "usage: backup.sh [env-file] [daily|pre-migration]"
      load_env_file "$1"
      shift
      mode=${1:-daily}
      ;;
  esac
fi
case "$mode" in
  daily|pre-migration) ;;
  *) die "mode must be daily or pre-migration" ;;
esac

require_env PGUSER
require_env PGDATABASE
require_env PGPASSWORD
require_env R2_ENDPOINT
require_env R2_BUCKET
require_env R2_BACKUP_ACCESS_KEY_ID
require_env R2_BACKUP_SECRET_ACCESS_KEY
reject_placeholders \
  PGPASSWORD \
  R2_ENDPOINT \
  R2_BUCKET \
  R2_BACKUP_ACCESS_KEY_ID \
  R2_BACKUP_SECRET_ACCESS_KEY

project=${BACKUP_PROJECT:-worklane-prod}
[[ "$project" =~ ^[A-Za-z0-9-]+$ ]] || die "BACKUP_PROJECT must be letters, digits, and hyphens"
retention=${BACKUP_RETENTION_DAYS:-30}
[[ "$retention" =~ ^[0-9]+$ ]] || die "BACKUP_RETENTION_DAYS must be a number"
if [[ "$retention" -gt 30 ]]; then
  die "BACKUP_RETENTION_DAYS is ${retention}. Beta backups stay at 30 days or fewer."
fi

suffix=dump
if [[ -n "${BACKUP_AGE_RECIPIENT:-}" ]]; then
  reject_placeholders BACKUP_AGE_RECIPIENT
  command -v age >/dev/null 2>&1 || die "age is not installed"
  suffix=dump.age
elif [[ -n "${BACKUP_GPG_RECIPIENT:-}" ]]; then
  reject_placeholders BACKUP_GPG_RECIPIENT
  command -v gpg >/dev/null 2>&1 || die "gpg is not installed"
  suffix=dump.gpg
elif [[ "${BACKUP_ALLOW_UNENCRYPTED:-}" == "1" ]]; then
  suffix=dump
else
  die "set BACKUP_AGE_RECIPIENT or BACKUP_GPG_RECIPIENT, or BACKUP_ALLOW_UNENCRYPTED=1 for a drill"
fi

if [[ -n "${BETTERSTACK_HEARTBEAT_URL:-}" ]]; then
  reject_placeholders BETTERSTACK_HEARTBEAT_URL
fi

ok=0
workdir=$(mktemp -d)
chmod 700 "$workdir"
cleanup() {
  local status=$?
  if [[ "$ok" != 1 && -n "${BETTERSTACK_HEARTBEAT_URL:-}" ]]; then
    ping_heartbeat "${BETTERSTACK_HEARTBEAT_URL%/}/fail" || true
  fi
  rm -rf "$workdir"
  exit "$status"
}
trap cleanup EXIT

stamp=$(date -u +%Y%m%dT%H%M%SZ)
year=$(date -u +%Y)
month=$(date -u +%m)
day=$(date -u +%d)
prefix=daily
if [[ "$mode" == "pre-migration" ]]; then
  prefix=pre-migration
fi
key="${prefix}/${project}/${year}/${month}/${day}/${project}-${stamp}.${suffix}"
validate_object_key "$key"

run_pg_dump >"$workdir/dump"
[[ -s "$workdir/dump" ]] || die "pg_dump wrote an empty file"

if [[ -n "${BACKUP_DOCKER_CONTAINER:-}" ]]; then
  docker cp "$workdir/dump" "${BACKUP_DOCKER_CONTAINER}:/tmp/worklane-dump-check"
  docker exec "$BACKUP_DOCKER_CONTAINER" pg_restore -l /tmp/worklane-dump-check >/dev/null
  docker exec "$BACKUP_DOCKER_CONTAINER" rm -f /tmp/worklane-dump-check
elif [[ -n "${WORKLANE_COMPOSE_FILE:-}" ]]; then
  compose cp "$workdir/dump" postgres:/tmp/worklane-dump-check
  compose exec -T postgres pg_restore -l /tmp/worklane-dump-check >/dev/null
  compose exec -T postgres rm -f /tmp/worklane-dump-check
else
  pg_restore -l "$workdir/dump" >/dev/null
fi

payload=$workdir/dump
if [[ "$suffix" == "dump.age" ]]; then
  age -r "$BACKUP_AGE_RECIPIENT" -o "$workdir/dump.age" "$workdir/dump"
  rm -f "$workdir/dump"
  payload=$workdir/dump.age
elif [[ "$suffix" == "dump.gpg" ]]; then
  gpg --batch --yes --trust-model always --encrypt \
    --recipient "$BACKUP_GPG_RECIPIENT" \
    --output "$workdir/dump.gpg" \
    "$workdir/dump"
  rm -f "$workdir/dump"
  payload=$workdir/dump.gpg
fi

upload_object "$payload" "$key" "$R2_BACKUP_ACCESS_KEY_ID" "$R2_BACKUP_SECRET_ACCESS_KEY"

if [[ -n "${BETTERSTACK_HEARTBEAT_URL:-}" ]]; then
  ping_heartbeat "$BETTERSTACK_HEARTBEAT_URL"
fi

ok=1
printf 'BACKUP_OBJECT=%s\n' "$key"
