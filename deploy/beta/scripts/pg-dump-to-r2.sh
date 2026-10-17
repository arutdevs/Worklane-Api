#!/usr/bin/env bash
# pg_dump -Fc, optional age encryption, upload to R2 under the SA prefixes.
# Pings a Better Stack heartbeat only after the upload succeeds.
#   pg-dump-to-r2.sh <env-file> [daily|pre-migration]
#
# WAL-G point-in-time recovery is not part of this script. See the README.
set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
# shellcheck source=lib.sh
source "$SCRIPT_DIR/lib.sh"

ENV_FILE=$(resolve_env_file "${1:-}")
dump_mode=${2:-daily}
case "$dump_mode" in
  daily|pre-migration) ;;
  *) die "mode must be daily or pre-migration" ;;
esac

"$SCRIPT_DIR/preflight.sh" --backup "$ENV_FILE"

project=$(require_env COMPOSE_PROJECT_NAME)
db=$(require_ident POSTGRES_DB)
readonly_role=$(require_ident POSTGRES_READONLY_USER)
account=$(require_env R2_ACCOUNT_ID)
bucket=$(require_env R2_BACKUP_BUCKET)
image=$(optional_env AWS_CLI_IMAGE)
[[ -n "$image" ]] || image="amazon/aws-cli:2.38.1"
endpoint="https://${account}.r2.cloudflarestorage.com"

if [[ "$dump_mode" == "pre-migration" ]]; then
  heartbeat=$(optional_env BETTERSTACK_HEARTBEAT_URL_PRE_MIGRATION)
else
  heartbeat=$(require_env BETTERSTACK_HEARTBEAT_URL_BACKUP)
fi

ok=0
workdir=$(mktemp -d)
awsenv=$(mktemp)
chmod 700 "$workdir"
write_aws_env "$awsenv" "$(require_env R2_BACKUP_ACCESS_KEY_ID)" "$(require_env R2_BACKUP_SECRET_ACCESS_KEY)"

ping() {
  local url=$1
  [[ -n "$url" ]] || return 0
  if ! curl -fsS -m 15 -o /dev/null "$url"; then
    printf 'warning: heartbeat ping failed (URL not logged)\n' >&2
    return 1
  fi
}

cleanup() {
  local status=$?
  if [[ "$ok" != 1 && -n "${heartbeat:-}" ]]; then
    ping "${heartbeat%/}/fail" || true
  fi
  rm -rf "$workdir"
  rm -f "$awsenv"
  exit "$status"
}
trap cleanup EXIT

compose up -d --wait postgres

role_exists=$(compose exec -T postgres psql -q -tA -U "$(require_ident POSTGRES_USER)" -d "$db" \
  -c "SELECT 1 FROM pg_roles WHERE rolname = '${readonly_role}'")
role_exists=${role_exists//[[:space:]]/}
[[ "$role_exists" == "1" ]] || die "role ${readonly_role} does not exist. Run init-db-roles.sh first."

stamp=$(date -u +%Y%m%dT%H%M%SZ)
year=$(date -u +%Y)
month=$(date -u +%m)
day=$(date -u +%d)
week=$(date -u +%G-W%V)
dow=$(date -u +%u)
dom=$(date -u +%d)

allow=$(optional_env BACKUP_ALLOW_UNENCRYPTED)
suffix="dump.age"
if [[ "$allow" == "1" ]]; then
  suffix="dump"
fi
object="${project}-${stamp}.${suffix}"

if [[ "$dump_mode" == "pre-migration" ]]; then
  key="pre-migration/${project}/${year}/${month}/${day}/${object}"
else
  key="daily/${project}/${year}/${month}/${day}/${object}"
fi

printf 'dumping %s as %s\n' "$project" "$readonly_role"
compose exec -T postgres pg_dump -Fc --no-owner --no-acl -U "$readonly_role" -d "$db" >"$workdir/dump"
[[ -s "$workdir/dump" ]] || die "pg_dump wrote an empty file"

compose cp "$workdir/dump" postgres:/tmp/worklane-dump-check
compose exec -T postgres chmod 644 /tmp/worklane-dump-check
compose exec -T postgres pg_restore -l /tmp/worklane-dump-check >/dev/null
compose exec -T postgres rm -f /tmp/worklane-dump-check

upload_name="dump"
if [[ "$allow" != "1" ]]; then
  age -r "$(require_env BACKUP_AGE_RECIPIENT)" -o "$workdir/dump.age" "$workdir/dump"
  shred -u "$workdir/dump" 2>/dev/null || rm -f "$workdir/dump"
  upload_name="dump.age"
fi

printf 'uploading s3://%s/%s\n' "$bucket" "$key"
r2 "$awsenv" "$workdir" "$image" "$endpoint" s3 cp "/work/${upload_name}" "s3://${bucket}/${key}"

if [[ "$dump_mode" == "daily" && "$dow" == "7" ]]; then
  weekly_key="weekly/${project}/${week}/${object}"
  printf 'copying s3://%s/%s\n' "$bucket" "$weekly_key"
  r2 "$awsenv" "$workdir" "$image" "$endpoint" s3 cp "s3://${bucket}/${key}" "s3://${bucket}/${weekly_key}"
fi
if [[ "$dump_mode" == "daily" && "$dom" == "01" ]]; then
  monthly_key="monthly/${project}/${year}/${month}/${object}"
  printf 'copying s3://%s/%s\n' "$bucket" "$monthly_key"
  r2 "$awsenv" "$workdir" "$image" "$endpoint" s3 cp "s3://${bucket}/${key}" "s3://${bucket}/${monthly_key}"
fi

if [[ -n "$heartbeat" ]]; then
  ping "$heartbeat"
else
  printf 'warning: no pre-migration heartbeat URL set, success was not pinged\n' >&2
fi

ok=1
printf 'backup ok %s\n' "$key"
