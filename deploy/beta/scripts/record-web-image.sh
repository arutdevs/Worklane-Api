#!/usr/bin/env bash
# Record a Worklane-Web image that is already loaded locally.
# The tag must be worklane-web:<slot>-<40 hex sha of the reviewed Web commit>.
# Draft pin: Worklane-Web PR #7 head 8bf129286373546443b2804feb622bfa67d9c509
# (CI green). Re-pin this to the merged Web commit before any deploy.
# This repo cannot see that private repo; the operator supplies the
# SHA they reviewed and the local image id. The image must be nginx-unprivileged
# on 8080 as uid 101. Compose waits for a healthy api so nginx can resolve `api`.
#   record-web-image.sh /etc/worklane/prod.env <40-hex-sha>
set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
# shellcheck source=lib.sh
source "$SCRIPT_DIR/lib.sh"

ENV_FILE=$(resolve_env_file "${1:-}")
sha=${2:-}
"$SCRIPT_DIR/preflight.sh" --up "$ENV_FILE"
require_full_sha "Worklane-Web commit" "$sha"

slot=$(env_slot)
image="worklane-web:${slot}-${sha}"
id=$(image_id_of "$image")
web_user=$(docker image inspect --format '{{.Config.User}}' "$image")
[[ "$web_user" == "101" ]] || die "Worklane-Web image Config.User must be 101 (got ${web_user:-empty})"
web_ports=$(docker image inspect --format '{{json .Config.ExposedPorts}}' "$image")
[[ "$web_ports" == *'"8080/tcp"'* ]] || die "Worklane-Web image must expose 8080/tcp"
lock_set WEB_COMMIT "$sha"
lock_set WEB_IMAGE "$image"
lock_set WEB_IMAGE_ID "$id"
verify_locked_image WEB
printf 'recorded %s id %s in %s\n' "$image" "$id" "$(image_lock_file)"
