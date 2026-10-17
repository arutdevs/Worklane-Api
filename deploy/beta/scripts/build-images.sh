#!/usr/bin/env bash
# Build the API and the migrator for one environment and record their image ids.
# Tags are worklane-api:<slot>-<commit> and worklane-migrator:<slot>-<commit>.
# Does not build Worklane-Web. Does not start containers.
#   build-images.sh /etc/worklane/prod.env
set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
# shellcheck source=lib.sh
source "$SCRIPT_DIR/lib.sh"

ENV_FILE=$(resolve_env_file "${1:-}")
"$SCRIPT_DIR/preflight.sh" --up "$ENV_FILE"

build_api_image
build_migrator_image
printf 'recorded %s\n' "$(image_lock_file)"
printf 'Next: tag worklane-web:%s-<web sha> from the reviewed Worklane-Web commit, then record-web-image.sh\n' "$(env_slot)"
