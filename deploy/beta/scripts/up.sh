#!/usr/bin/env bash
# Start one beta compose project. Refuses the leaked JWT and refuses to boot
# until deploy/beta/WL63_DONE exists (see the README). Does not create cloud accounts.
# Does not build. Images come from the lock file beside the env file.
#   up.sh /etc/worklane/prod.env
set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
# shellcheck source=lib.sh
source "$SCRIPT_DIR/lib.sh"

ENV_FILE=$(resolve_env_file "${1:-}")
"$SCRIPT_DIR/preflight.sh" --up "$ENV_FILE"

other=$(sibling_env_file || true)
if [[ -n "$other" && -f "$other" ]]; then
  "$SCRIPT_DIR/check-env-isolation.sh" "$ENV_FILE" "$other"
fi

gate="$(deploy_dir)/WL63_DONE"
if [[ ! -f "$gate" ]] || ! awk 'NR==1 { gsub(/\r/, ""); print $1 }' "$gate" | grep -qx 'yes'; then
  die "WL-63 is not marked done. Create $gate whose first line starts with 'yes' only after the image you are about to run rejects the leaked JWT. See deploy/beta/README.md."
fi

# The marker above is a speed bump. The lock is what selects the image.
[[ -f "$(image_lock_file)" ]] || die "image lock not found: $(image_lock_file). Run build-images.sh, then record-web-image.sh."
write_image_override 1

rendered=$(mktemp)
trap 'rm -f "$rendered"' EXIT
compose config --format json >"$rendered"
assert_locked_service_image "$rendered" api API
assert_locked_service_image "$rendered" web WEB
compose --profile migrate config --format json >"$rendered"
assert_locked_service_image "$rendered" migrator MIGRATOR
rm -f "$rendered"
trap - EXIT

# A local sentinel tag lets `docker compose up` without the override start it.
if docker image ls --format '{{.Repository}}:{{.Tag}}' | grep -Eq '^worklane-.+:unlocked-do-not-run$'; then
  die "refusing to start: a worklane-*:unlocked-do-not-run image exists locally. Remove it, and remove any old worklane-*:beta tags."
fi

printf 'Starting %s with %s. This boots local containers only.\n' \
  "$(require_env COMPOSE_PROJECT_NAME)" "$(require_lock API_IMAGE)"
compose up -d --no-build --wait --remove-orphans
compose ps
