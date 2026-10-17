#!/usr/bin/env bash
# Download one dump from R2 and pg_restore it into this compose project's database.
# Stops api and web first. Does not start them again.
#   restore-from-r2.sh <env-file> <object-key> --yes [--clean]
set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
# shellcheck source=lib.sh
source "$SCRIPT_DIR/lib.sh"

ENV_FILE=$(resolve_env_file "${1:-}")
key=${2:-}
confirm=${3:-}
clean=0
if [[ "${4:-}" == "--clean" || "$confirm" == "--clean" ]]; then
  clean=1
fi
[[ "$confirm" == "--yes" || "${4:-}" == "--yes" ]] || die "usage: restore-from-r2.sh <env-file> <object-key> --yes [--clean]"
[[ "$key" =~ ^(daily|weekly|monthly|pre-migration)/[A-Za-z0-9._/-]+$ ]] || die "object key must sit under daily/, weekly/, monthly/, or pre-migration/"
[[ "$key" != *..* ]] || die "object key must not contain .."

"$SCRIPT_DIR/preflight.sh" --backup "$ENV_FILE"

project=$(require_env COMPOSE_PROJECT_NAME)
db=$(require_ident POSTGRES_DB)
admin=$(require_ident POSTGRES_USER)
migrator=$(require_ident POSTGRES_MIGRATOR_USER)
account=$(require_env R2_ACCOUNT_ID)
bucket=$(require_env R2_BACKUP_BUCKET)
image=$(optional_env AWS_CLI_IMAGE)
[[ -n "$image" ]] || image="amazon/aws-cli:2.38.1"
endpoint="https://${account}.r2.cloudflarestorage.com"

umask 077
workdir=$(mktemp -d)
awsenv=$(mktemp)
chmod 700 "$workdir"
trap 'rm -rf "$workdir"; rm -f "$awsenv"' EXIT
write_aws_env "$awsenv" "$(require_env R2_BACKUP_ACCESS_KEY_ID)" "$(require_env R2_BACKUP_SECRET_ACCESS_KEY)"

printf 'stopping api and web on %s\n' "$project"
compose stop api web || true
compose up -d --wait postgres

local_name="download"
r2 "$awsenv" "$workdir" "$image" "$endpoint" s3 cp "s3://${bucket}/${key}" "/work/${local_name}"

restore_file="$workdir/$local_name"
if [[ "$key" == *.age ]]; then
  identity=$(require_env BACKUP_AGE_IDENTITY_FILE)
  [[ -f "$identity" ]] || die "BACKUP_AGE_IDENTITY_FILE does not exist"
  perm=$(stat -c %a "$identity")
  if (( (8#$perm & 8#077) != 0 )); then
    die "BACKUP_AGE_IDENTITY_FILE is readable by group or others (chmod 600)"
  fi
  command -v age >/dev/null 2>&1 || die "age is not installed"
  age -d -i "$identity" -o "$workdir/dump" "$restore_file"
  shred -u "$restore_file" 2>/dev/null || rm -f "$restore_file"
  restore_file="$workdir/dump"
fi

compose cp "$restore_file" postgres:/tmp/worklane-restore.dump
compose exec -T postgres chmod 644 /tmp/worklane-restore.dump
compose exec -T postgres pg_restore -l /tmp/worklane-restore.dump >/dev/null

has_history=$(compose exec -T postgres psql -q -tA -U "$admin" -d "$db" \
  -c "SELECT 1 FROM pg_tables WHERE schemaname = 'public' AND tablename = '__EFMigrationsHistory'")
if [[ "$has_history" == "1" && "$clean" != 1 ]]; then
  compose exec -T postgres rm -f /tmp/worklane-restore.dump
  die "database already has __EFMigrationsHistory. Pass --clean to drop and recreate objects, or restore into an empty volume."
fi

restore_args=(pg_restore --no-owner --no-acl --exit-on-error -U "$migrator" -d "$db" /tmp/worklane-restore.dump)
if [[ "$clean" == 1 ]]; then
  restore_args=(pg_restore --clean --if-exists --no-owner --no-acl --exit-on-error -U "$migrator" -d "$db" /tmp/worklane-restore.dump)
fi
compose exec -T postgres "${restore_args[@]}"
compose exec -T postgres rm -f /tmp/worklane-restore.dump

"$SCRIPT_DIR/init-db-roles.sh" "$ENV_FILE"
printf 'restore finished for %s from %s\n' "$project" "$key"
printf 'api and web are still stopped. Pull the attachment mirror if you use one, then run up.sh.\n'
