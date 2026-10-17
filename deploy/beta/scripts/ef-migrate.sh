#!/usr/bin/env bash
# Pre-migration dump, then the migrations bundle as the migrator role.
#   ef-migrate.sh <env-file> --yes
set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
# shellcheck source=lib.sh
source "$SCRIPT_DIR/lib.sh"

ENV_FILE=$(resolve_env_file "${1:-}")
[[ "${2:-}" == "--yes" ]] || die "usage: ef-migrate.sh <env-file> --yes"

"$SCRIPT_DIR/preflight.sh" --up "$ENV_FILE"
"$SCRIPT_DIR/preflight.sh" --backup "$ENV_FILE"

compose up -d --wait postgres
"$SCRIPT_DIR/init-db-roles.sh" "$ENV_FILE"
"$SCRIPT_DIR/pg-dump-to-r2.sh" "$ENV_FILE" pre-migration

printf 'applying EF migrations as %s\n' "$(require_ident POSTGRES_MIGRATOR_USER)"
# The migrator image already contains the bundle. Nothing is installed here.
compose --profile migrate run --rm migrator

"$SCRIPT_DIR/init-db-roles.sh" "$ENV_FILE"

if compose ps --status running --services 2>/dev/null | grep -qx api; then
  printf 'restarting api so it picks up the schema\n'
  compose restart api
fi
printf 'migrations applied\n'
