#!/usr/bin/env bash
# Pre-migration dump, then `dotnet ef database update` as the migrator role.
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
compose --profile migrate run --rm migrator \
  'dotnet tool install --tool-path /tmp/tools dotnet-ef --version 10.0.13 && export PATH="/tmp/tools:$PATH" && dotnet ef database update --project src/Worklane.Infrastructure/Worklane.Infrastructure.csproj --startup-project src/Worklane.WebApi/Worklane.WebApi.csproj'

"$SCRIPT_DIR/init-db-roles.sh" "$ENV_FILE"

if compose ps --status running --services 2>/dev/null | grep -qx api; then
  printf 'restarting api so it picks up the schema\n'
  compose restart api
fi
printf 'migrations applied\n'
