#!/usr/bin/env bash
# Start one beta compose project. Refuses the leaked JWT and refuses to boot
# until deploy/beta/WL63_DONE exists (see the README). Does not create cloud accounts.
#   up.sh path/to/.env.prod
set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
# shellcheck source=lib.sh
source "$SCRIPT_DIR/lib.sh"

ENV_FILE=$(resolve_env_file "${1:-}")
"$SCRIPT_DIR/preflight.sh" --up "$ENV_FILE"

base=$(basename "$ENV_FILE")
dir=$(dirname "$ENV_FILE")
other=""
case "$base" in
  .env.prod) other="$dir/.env.staging" ;;
  .env.staging) other="$dir/.env.prod" ;;
esac
if [[ -n "$other" && -f "$other" ]]; then
  "$SCRIPT_DIR/check-env-isolation.sh" "$ENV_FILE" "$other"
fi

gate="$(deploy_dir)/WL63_DONE"
if [[ ! -f "$gate" ]] || ! awk 'NR==1 { gsub(/\r/, ""); print $1 }' "$gate" | grep -qx 'yes'; then
  die "WL-63 is not marked done. Create $gate whose first line starts with 'yes' only after the image you are about to run rejects the leaked JWT. See deploy/beta/README.md."
fi

printf 'Starting %s. This boots local containers only.\n' "$(require_env COMPOSE_PROJECT_NAME)"
compose up -d --build --wait --remove-orphans
compose ps
