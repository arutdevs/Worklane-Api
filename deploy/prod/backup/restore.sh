#!/usr/bin/env bash
# Download one dump and pg_restore it. Stops api, web, and edge when
# WORKLANE_COMPOSE_FILE is set, and does not start them again.
#
#   restore.sh [env-file] <object-key> --yes [--clean]
#
# Uses the restore token (GetObject), not the daily write token.
# --clean passes pg_restore --clean --if-exists. Without it, a database that
# already has the dumped objects is left untouched and this script exits 1.
set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
# shellcheck source=deploy/prod/backup/lib.sh
source "$SCRIPT_DIR/lib.sh"

if [[ $# -ge 1 && -f "$1" ]]; then
  load_env_file "$1"
  shift
fi

key=${1:-}
[[ -n "$key" ]] || die "usage: restore.sh [env-file] <object-key> --yes [--clean]"
shift

confirm=0
clean=0
while [[ $# -gt 0 ]]; do
  case "$1" in
    --yes) confirm=1 ;;
    --clean) clean=1 ;;
    *) die "unknown argument: $1" ;;
  esac
  shift
done
[[ "$confirm" == 1 ]] || die "refusing to restore without --yes"
validate_object_key "$key"

require_env PGUSER
require_env PGDATABASE
require_env PGPASSWORD
require_env R2_ENDPOINT
require_env R2_BUCKET
require_env R2_RESTORE_ACCESS_KEY_ID
require_env R2_RESTORE_SECRET_ACCESS_KEY
reject_placeholders \
  PGPASSWORD \
  R2_ENDPOINT \
  R2_BUCKET \
  R2_RESTORE_ACCESS_KEY_ID \
  R2_RESTORE_SECRET_ACCESS_KEY

if [[ "${BACKUP_DRILL:-}" != "1" ]]; then
  if [[ "$R2_RESTORE_ACCESS_KEY_ID" == "${R2_BACKUP_ACCESS_KEY_ID:-}" ]]; then
    die "restore token must differ from the daily write token (set BACKUP_DRILL=1 only for a local drill)"
  fi
fi

workdir=$(mktemp -d)
chmod 700 "$workdir"
trap 'rm -rf "$workdir"' EXIT

download_object "$key" "$workdir/download" "$R2_RESTORE_ACCESS_KEY_ID" "$R2_RESTORE_SECRET_ACCESS_KEY"
[[ -s "$workdir/download" ]] || die "downloaded object is empty"

restore_file=$workdir/download
case "$key" in
  *.age)
    [[ -n "${BACKUP_AGE_IDENTITY_FILE:-}" ]] || die "BACKUP_AGE_IDENTITY_FILE is required"
    [[ -f "$BACKUP_AGE_IDENTITY_FILE" && ! -L "$BACKUP_AGE_IDENTITY_FILE" ]] || die "age identity file is missing"
    perm=$(stat -c %a "$BACKUP_AGE_IDENTITY_FILE")
    if (( (8#$perm & 8#077) != 0 )); then
      die "age identity file is readable by group or others"
    fi
    command -v age >/dev/null 2>&1 || die "age is not installed"
    age -d -i "$BACKUP_AGE_IDENTITY_FILE" -o "$workdir/dump" "$restore_file"
    rm -f "$restore_file"
    restore_file=$workdir/dump
    ;;
  *.gpg)
    command -v gpg >/dev/null 2>&1 || die "gpg is not installed"
    gpg --batch --yes --decrypt --output "$workdir/dump" "$restore_file"
    rm -f "$restore_file"
    restore_file=$workdir/dump
    ;;
esac

if [[ -n "${WORKLANE_COMPOSE_FILE:-}" ]]; then
  compose stop api web edge
  compose cp "$restore_file" postgres:/tmp/worklane-restore.dump
  compose exec -T postgres chmod 644 /tmp/worklane-restore.dump
  compose exec -T postgres pg_restore -l /tmp/worklane-restore.dump >/dev/null
  restore_cmd=(pg_restore --no-owner --no-acl --exit-on-error -h 127.0.0.1 -p "${PGPORT:-5432}" -U "$PGUSER" -d "$PGDATABASE" /tmp/worklane-restore.dump)
  if [[ "$clean" == 1 ]]; then
    restore_cmd=(pg_restore --clean --if-exists --no-owner --no-acl --exit-on-error -h 127.0.0.1 -p "${PGPORT:-5432}" -U "$PGUSER" -d "$PGDATABASE" /tmp/worklane-restore.dump)
  fi
  compose exec -T -e "PGPASSWORD=${PGPASSWORD}" postgres "${restore_cmd[@]}"
  compose exec -T postgres rm -f /tmp/worklane-restore.dump
elif [[ -n "${BACKUP_DOCKER_CONTAINER:-}" ]]; then
  docker cp "$restore_file" "${BACKUP_DOCKER_CONTAINER}:/tmp/worklane-restore.dump"
  docker exec "$BACKUP_DOCKER_CONTAINER" chmod 644 /tmp/worklane-restore.dump
  docker exec "$BACKUP_DOCKER_CONTAINER" pg_restore -l /tmp/worklane-restore.dump >/dev/null
  restore_cmd=(pg_restore --no-owner --no-acl --exit-on-error -h 127.0.0.1 -p "${PGPORT:-5432}" -U "$PGUSER" -d "$PGDATABASE" /tmp/worklane-restore.dump)
  if [[ "$clean" == 1 ]]; then
    restore_cmd=(pg_restore --clean --if-exists --no-owner --no-acl --exit-on-error -h 127.0.0.1 -p "${PGPORT:-5432}" -U "$PGUSER" -d "$PGDATABASE" /tmp/worklane-restore.dump)
  fi
  docker exec -e "PGPASSWORD=${PGPASSWORD}" "$BACKUP_DOCKER_CONTAINER" "${restore_cmd[@]}"
  docker exec "$BACKUP_DOCKER_CONTAINER" rm -f /tmp/worklane-restore.dump
else
  require_env PGHOST
  pg_restore -l "$restore_file" >/dev/null
  restore_cmd=(pg_restore --no-owner --no-acl --exit-on-error -h "$PGHOST" -p "${PGPORT:-5432}" -U "$PGUSER" -d "$PGDATABASE" "$restore_file")
  if [[ "$clean" == 1 ]]; then
    restore_cmd=(pg_restore --clean --if-exists --no-owner --no-acl --exit-on-error -h "$PGHOST" -p "${PGPORT:-5432}" -U "$PGUSER" -d "$PGDATABASE" "$restore_file")
  fi
  PGPASSWORD="$PGPASSWORD" "${restore_cmd[@]}"
fi

printf 'RESTORE_OK %s\n' "$key"
if [[ -n "${WORKLANE_COMPOSE_FILE:-}" ]]; then
  printf 'api, web, and edge are still stopped\n'
fi
