#!/usr/bin/env bash
# Stopgap mirror of the local attachment volume to R2 until B-18.
# The API does not read these objects. A restore must copy them back onto the volume
# before the API starts.
#   sync-attachments-to-r2.sh <env-file> [push|pull]
set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
# shellcheck source=lib.sh
source "$SCRIPT_DIR/lib.sh"

ENV_FILE=$(resolve_env_file "${1:-}")
direction=${2:-push}
case "$direction" in
  push|pull) ;;
  *) die "direction must be push or pull" ;;
esac

"$SCRIPT_DIR/preflight.sh" --up "$ENV_FILE"

project=$(require_env COMPOSE_PROJECT_NAME)
account=$(require_env R2_ACCOUNT_ID)
bucket=$(require_env R2_ATTACHMENTS_BUCKET)
reject_placeholder R2_ACCOUNT_ID "$account"
reject_placeholder R2_ATTACHMENTS_BUCKET "$bucket"
[[ "$bucket" =~ ^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$ ]] || die "R2_ATTACHMENTS_BUCKET is not a safe bucket name"
access=$(require_env R2_ATTACHMENTS_ACCESS_KEY_ID)
secret=$(require_env R2_ATTACHMENTS_SECRET_ACCESS_KEY)
reject_placeholder R2_ATTACHMENTS_ACCESS_KEY_ID "$access"
reject_placeholder R2_ATTACHMENTS_SECRET_ACCESS_KEY "$secret"
reject_unsafe_secret R2_ATTACHMENTS_ACCESS_KEY_ID "$access"
reject_unsafe_secret R2_ATTACHMENTS_SECRET_ACCESS_KEY "$secret"

backup_bucket=$(optional_env R2_BACKUP_BUCKET)
[[ -z "$backup_bucket" || "$backup_bucket" != "$bucket" ]] || die "attachment bucket must be different from the backup bucket"

image=$(optional_env AWS_CLI_IMAGE)
[[ -n "$image" ]] || image="amazon/aws-cli:2.38.1"
endpoint="https://${account}.r2.cloudflarestorage.com"
prefix="mirror/${project}"

vol=$(docker volume ls -q \
  --filter "label=com.docker.compose.project=${project}" \
  --filter "label=com.docker.compose.volume=attachments")
[[ -n "$vol" ]] || die "attachments volume for ${project} was not found. Start the stack once so compose creates it."
[[ "$(printf '%s\n' "$vol" | wc -l)" -eq 1 ]] || die "more than one attachments volume matched ${project}"

heartbeat=$(optional_env BETTERSTACK_HEARTBEAT_URL_ATTACHMENTS)
ok=0
workdir=$(mktemp -d)
awsenv=$(mktemp)
chmod 700 "$workdir"
write_aws_env "$awsenv" "$access" "$secret"

cleanup() {
  local status=$?
  if [[ "$ok" != 1 && -n "$heartbeat" ]]; then
    curl -fsS -m 15 -o /dev/null "${heartbeat%/}/fail" || true
  fi
  rm -rf "$workdir"
  rm -f "$awsenv"
  exit "$status"
}
trap cleanup EXIT

if [[ "$direction" == "push" ]]; then
  # No --delete: a bad local directory must not erase the only offsite copy.
  docker run --rm --env-file "$awsenv" -v "${vol}:/data:ro" "$image" \
    s3 sync /data "s3://${bucket}/${prefix}/" --endpoint-url "$endpoint"
else
  docker run --rm --env-file "$awsenv" -v "${vol}:/data" "$image" \
    s3 sync "s3://${bucket}/${prefix}/" /data --endpoint-url "$endpoint"
fi

if [[ -n "$heartbeat" ]]; then
  curl -fsS -m 15 -o /dev/null "$heartbeat" || die "attachment heartbeat ping failed"
fi
ok=1
printf 'attachment %s ok for %s (stopgap until B-18; the API still reads local disk)\n' "$direction" "$project"
