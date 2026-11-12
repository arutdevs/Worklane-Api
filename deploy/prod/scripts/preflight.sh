#!/usr/bin/env bash
# Refuses placeholder digests and CHANGE_ME / example.com in an env file.
# This is not the WL-63 control. A green run does not mean the ticket is merged.
#   preflight.sh [env-file]
set -euo pipefail

root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
compose=$root/docker-compose.prod.yml
fail=0

printf '%s\n' \
  "WL-63 gate: this script does not query GitHub." \
  "Production deploy stays blocked until WL-63 (JWT rotation) is merged." \
  "See docs/deploy/PROD-DEPLOY-PLAN.md."

if [[ ! -f "$compose" ]]; then
  printf 'preflight: compose file missing\n' >&2
  exit 1
fi

while IFS= read -r line; do
  case "$line" in
    *"@sha256:"*) ;;
    *)
      printf 'preflight: image is not pinned by digest: %s\n' "$line" >&2
      fail=1
      ;;
  esac
done < <(grep -E '^[[:space:]]*image:' "$compose")

if grep -E -n 'sha256:(.)\1{63}' "$compose"; then
  printf 'preflight: placeholder digests are still in the compose file\n' >&2
  fail=1
fi

if grep -E -n 'ports:' "$compose"; then
  printf 'preflight: a ports key is present; this draft publishes nothing\n' >&2
  fail=1
fi

env_file=${1:-}
if [[ -n "$env_file" ]]; then
  if [[ ! -f "$env_file" ]]; then
    printf 'preflight: env file missing\n' >&2
    exit 1
  fi
  if grep -E -n 'CHANGE_ME|example\.com|example\.r2\.cloudflarestorage\.com' "$env_file"; then
    printf 'preflight: env file still has placeholders\n' >&2
    fail=1
  fi
fi

exit "$fail"
