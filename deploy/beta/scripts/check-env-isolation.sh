#!/usr/bin/env bash
# Fail when prod and staging reuse a secret. Prints hashes only, never values.
#   check-env-isolation.sh <env-a> <env-b>
set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
# shellcheck source=lib.sh
source "$SCRIPT_DIR/lib.sh"

ENV_A=$(resolve_env_file "${1:-}")
ENV_B=$(resolve_env_file "${2:-}")
[[ "$ENV_A" != "$ENV_B" ]] || die "the two env files are the same path"

keys=(
  JWT_SECRET
  POSTGRES_PASSWORD
  POSTGRES_APP_PASSWORD
  POSTGRES_MIGRATOR_PASSWORD
  POSTGRES_READONLY_PASSWORD
  CLOUDFLARE_TUNNEL_TOKEN
  SMTP_PASSWORD
  R2_BACKUP_SECRET_ACCESS_KEY
  R2_ATTACHMENTS_SECRET_ACCESS_KEY
)

for key in "${keys[@]}"; do
  ENV_FILE=$ENV_A
  a=$(optional_env "$key")
  ENV_FILE=$ENV_B
  b=$(optional_env "$key")
  [[ -n "$a" && -n "$b" ]] || continue
  if [[ "$(sha256_text "$a")" == "$(sha256_text "$b")" ]]; then
    die "$key is identical in $ENV_A and $ENV_B"
  fi
done

printf 'prod and staging secrets differ\n'
