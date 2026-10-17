#!/usr/bin/env bash
# Secret and WL-63 checks for a beta env file. Does not print secret values.
#   preflight.sh --up <env-file>
#   preflight.sh --backup <env-file>
set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
# shellcheck source=lib.sh
source "$SCRIPT_DIR/lib.sh"

mode=${1:-}
ENV_FILE=$(resolve_env_file "${2:-}")

case "$mode" in
  --up|--backup) ;;
  *) die "usage: preflight.sh --up|--backup <env-file>" ;;
esac

denylist="$(deploy_dir)/leaked-jwt-sha256.txt"
[[ -f "$denylist" ]] || die "missing $denylist"

project=$(require_env COMPOSE_PROJECT_NAME)
case "$project" in
  worklane-prod|worklane-staging) ;;
  *) die "COMPOSE_PROJECT_NAME must be worklane-prod or worklane-staging" ;;
esac

require_ident POSTGRES_DB >/dev/null
require_ident POSTGRES_USER >/dev/null
require_ident POSTGRES_APP_USER >/dev/null
require_ident POSTGRES_MIGRATOR_USER >/dev/null
require_ident POSTGRES_READONLY_USER >/dev/null

check_secret() {
  local key=$1 val
  val=$(require_env "$key")
  reject_placeholder "$key" "$val"
  reject_unsafe_secret "$key" "$val"
  printf '%s' "$(sha256_text "$val")"
}

hashes=()
for key in POSTGRES_PASSWORD POSTGRES_APP_PASSWORD POSTGRES_MIGRATOR_PASSWORD POSTGRES_READONLY_PASSWORD; do
  hashes+=("$(check_secret "$key")")
done

if [[ "$mode" == "--up" ]]; then
  platform=$(require_env DOCKER_PLATFORM)
  case "$platform" in
    linux/arm64|linux/amd64) ;;
    *) die "DOCKER_PLATFORM must be linux/arm64 or linux/amd64" ;;
  esac

  environment=$(require_env ASPNETCORE_ENVIRONMENT)
  case "$project:$environment" in
    worklane-prod:Production|worklane-staging:Staging) ;;
    *) die "ASPNETCORE_ENVIRONMENT must be Production for worklane-prod and Staging for worklane-staging" ;;
  esac

  origin=$(require_env PUBLIC_ORIGIN)
  reject_placeholder PUBLIC_ORIGIN "$origin"
  [[ "$origin" == https://* ]] || die "PUBLIC_ORIGIN must be an https URL"
  [[ "$origin" != */ ]] || die "PUBLIC_ORIGIN must not have a trailing slash"

  hosts=$(require_env ALLOWED_HOSTS)
  reject_placeholder ALLOWED_HOSTS "$hosts"
  origin_host=${origin#https://}
  [[ "$hosts" == "$origin_host" ]] || die "ALLOWED_HOSTS must be exactly the host of PUBLIC_ORIGIN"

  jwt=$(require_env JWT_SECRET)
  reject_placeholder JWT_SECRET "$jwt"
  reject_unsafe_secret JWT_SECRET "$jwt"
  jwt_hash=$(sha256_text "$jwt")
  if grep -vE '^[[:space:]]*#' "$denylist" | awk 'NF {print $1}' | grep -qx "$jwt_hash"; then
    die "JWT_SECRET is the leaked dev signing secret (WL-63). Generate a new one. The old value stays burned."
  fi
  umask 077
  jwt_raw=$(mktemp)
  if ! printf '%s' "$jwt" | tr -d '[:space:]' | base64 -d >"$jwt_raw" 2>/dev/null; then
    rm -f "$jwt_raw"
    die "JWT_SECRET is not valid base64"
  fi
  jwt_len=$(wc -c <"$jwt_raw")
  rm -f "$jwt_raw"
  [[ "$jwt_len" -ge 32 ]] || die "JWT_SECRET must decode to at least 32 bytes (use: openssl rand -base64 64 | tr -d '\\n')"

  hashes+=("$jwt_hash")
  hashes+=("$(check_secret CLOUDFLARE_TUNNEL_TOKEN)")
  hashes+=("$(check_secret SMTP_PASSWORD)")

  web_image=$(require_env WEB_IMAGE)
  reject_placeholder WEB_IMAGE "$web_image"

  smtp_host=$(require_env SMTP_HOST)
  reject_placeholder SMTP_HOST "$smtp_host"

  # The two login roles, the bootstrap role, and the tunnel token must not be reused.
  unique=$(printf '%s\n' "${hashes[@]}" | sort | uniq | wc -l)
  [[ "$unique" -eq "${#hashes[@]}" ]] || die "JWT, database passwords, SMTP password, and tunnel token must all be different"

  if [[ -n "$(optional_env R2_BACKUP_ACCESS_KEY_ID)" && -n "$(optional_env R2_ATTACHMENTS_ACCESS_KEY_ID)" ]]; then
    backup_key=$(require_env R2_BACKUP_ACCESS_KEY_ID)
    attach_key=$(require_env R2_ATTACHMENTS_ACCESS_KEY_ID)
    [[ "$backup_key" != "$attach_key" ]] || die "R2 backup and attachment access keys must be different"
  fi
fi

if [[ "$mode" == "--backup" ]]; then
  account=$(require_env R2_ACCOUNT_ID)
  reject_placeholder R2_ACCOUNT_ID "$account"
  bucket=$(require_env R2_BACKUP_BUCKET)
  reject_placeholder R2_BACKUP_BUCKET "$bucket"
  [[ "$bucket" =~ ^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$ ]] || die "R2_BACKUP_BUCKET is not a safe bucket name"
  check_secret R2_BACKUP_ACCESS_KEY_ID >/dev/null
  check_secret R2_BACKUP_SECRET_ACCESS_KEY >/dev/null

  heartbeat=$(require_env BETTERSTACK_HEARTBEAT_URL_BACKUP)
  [[ "$heartbeat" == https://* ]] || die "BETTERSTACK_HEARTBEAT_URL_BACKUP must be an https URL"
  reject_placeholder BETTERSTACK_HEARTBEAT_URL_BACKUP "$heartbeat"

  allow=$(optional_env BACKUP_ALLOW_UNENCRYPTED)
  recipient=$(optional_env BACKUP_AGE_RECIPIENT)
  if [[ "$allow" == "1" ]]; then
    printf 'warning: BACKUP_ALLOW_UNENCRYPTED=1, dumps will be uploaded without age\n' >&2
  else
    [[ -n "$recipient" ]] || die "BACKUP_AGE_RECIPIENT is required (or set BACKUP_ALLOW_UNENCRYPTED=1 to refuse encryption on purpose)"
    [[ "$recipient" == age1* ]] || die "BACKUP_AGE_RECIPIENT must be an age public recipient (age1...)"
    command -v age >/dev/null 2>&1 || die "age is not installed (Ubuntu: sudo apt-get install -y age)"
  fi
fi

printf 'preflight %s ok for %s\n' "$mode" "$project"
