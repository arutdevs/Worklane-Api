# Shared helpers for the beta scripts. Sourced, not executed.
# Callers set -euo pipefail themselves.

deploy_dir() {
  cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd
}

repo_root() {
  cd "$(deploy_dir)/../.." && pwd
}

compose_file() {
  printf '%s/docker-compose.yml' "$(deploy_dir)"
}

die() {
  printf 'error: %s\n' "$1" >&2
  exit 1
}

# ENV_FILE is an absolute path set by the caller.
read_env() {
  local key=$1 line val
  [[ -n "${ENV_FILE:-}" && -f "$ENV_FILE" ]] || die "ENV_FILE is not set to a file"
  line=$(grep -E "^${key}=" "$ENV_FILE" | tail -n 1 || true)
  [[ -n "$line" ]] || return 1
  val=${line#*=}
  val=${val%$'\r'}
  if [[ "$val" == \"*\" && "$val" == *\" ]]; then
    val=${val#\"}
    val=${val%\"}
  elif [[ "$val" == \'*\' && "$val" == *\' ]]; then
    val=${val#\'}
    val=${val%\'}
  fi
  printf '%s' "$val"
}

require_env() {
  local key=$1 val
  if ! val=$(read_env "$key"); then
    die "$key is missing from $ENV_FILE"
  fi
  [[ -n "$val" ]] || die "$key is empty in $ENV_FILE"
  printf '%s' "$val"
}

optional_env() {
  local key=$1
  read_env "$key" 2>/dev/null || true
}

is_placeholder() {
  local v
  v=$(printf '%s' "$1" | tr '[:upper:]' '[:lower:]')
  case "$v" in
    ""|replace-me|changeme|change-me|change_me|password|worklane|postgres|secret|your-key|your-token|your-domain)
      return 0
      ;;
  esac
  [[ "$v" == *example.com* || "$v" == *example.invalid* ]] && return 0
  return 1
}

reject_placeholder() {
  local key=$1 val=$2
  if is_placeholder "$val"; then
    die "$key is still a placeholder"
  fi
}

# Identifiers interpolated into SQL. Passwords are escaped separately.
require_ident() {
  local key=$1 val
  val=$(require_env "$key")
  [[ "$val" =~ ^[a-z_][a-z0-9_]*$ ]] || die "$key must match [a-z_][a-z0-9_]* (got a value that is not a safe SQL identifier)"
  printf '%s' "$val"
}

reject_unsafe_secret() {
  local key=$1 val=$2
  # '$' and backticks would be expanded by the shell while building SQL or env files.
  if [[ "$val" == *"'"* || "$val" == *";"* || "$val" == *$'\n'* || "$val" == *$'\r'* || "$val" == *" "* || "$val" == *'$'* || "$val" == *'`'* || "$val" == *'\'* ]]; then
    die "$key must not contain spaces, quotes, dollar signs, backticks, backslashes, semicolons, or newlines"
  fi
}

sha256_text() {
  printf '%s' "$1" | sha256sum | awk '{print $1}'
}

compose() {
  docker compose --env-file "$ENV_FILE" -f "$(compose_file)" "$@"
}

write_aws_env() {
  local dest=$1 key=$2 secret=$3
  umask 077
  printf 'AWS_ACCESS_KEY_ID=%s\nAWS_SECRET_ACCESS_KEY=%s\nAWS_DEFAULT_REGION=auto\n' "$key" "$secret" >"$dest"
  chmod 600 "$dest"
}

# amazon/aws-cli entrypoint is already `aws`. Extra args are the aws subcommand.
r2() {
  local awsenv=$1 workdir=$2 image=$3 endpoint=$4
  shift 4
  docker run --rm --env-file "$awsenv" -v "$workdir:/work" "$image" "$@" --endpoint-url "$endpoint"
}

# Absolute path. Rejects a missing file.
resolve_env_file() {
  local path=$1
  [[ -n "$path" && -f "$path" ]] || die "env file not found: ${path:-<missing>}"
  local dir base
  dir=$(cd "$(dirname "$path")" && pwd)
  base=$(basename "$path")
  printf '%s/%s' "$dir" "$base"
}
