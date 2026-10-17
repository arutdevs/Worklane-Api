#!/usr/bin/env bash
# Pre-migration dump, then the migrations bundle as the migrator role.
# Builds the migrator from the same commit as the locked API image and checks
# the image id before running it. No --connection argument (see migrate.sh).
#   ef-migrate.sh /etc/worklane/prod.env --yes
set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
# shellcheck source=lib.sh
source "$SCRIPT_DIR/lib.sh"

ENV_FILE=$(resolve_env_file "${1:-}")
[[ "${2:-}" == "--yes" ]] || die "usage: ef-migrate.sh <env-file> --yes"

"$SCRIPT_DIR/preflight.sh" --up "$ENV_FILE"
"$SCRIPT_DIR/preflight.sh" --backup "$ENV_FILE"

build_migrator_image
write_image_override 0
verify_locked_image MIGRATOR

compose up -d --no-build --wait postgres
"$SCRIPT_DIR/init-db-roles.sh" "$ENV_FILE"
"$SCRIPT_DIR/pg-dump-to-r2.sh" "$ENV_FILE" pre-migration

printf 'applying EF migrations as %s using %s\n' \
  "$(require_ident POSTGRES_MIGRATOR_USER)" "$(require_lock MIGRATOR_IMAGE)"
# Re-check the id immediately before the container is created.
# `compose run` builds only when --build is passed. This Compose version has
# no `run --no-build` flag (`up` does).
verify_locked_image MIGRATOR
compose --profile migrate run --rm migrator

"$SCRIPT_DIR/init-db-roles.sh" "$ENV_FILE"

if compose ps --status running --services 2>/dev/null | grep -qx api; then
  printf 'restarting api so it picks up the schema\n'
  compose restart api
fi
printf 'migrations applied\n'
