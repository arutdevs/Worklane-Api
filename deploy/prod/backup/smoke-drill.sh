#!/usr/bin/env bash
# Local proof that backup.sh and restore.sh dump, encrypt, upload, and restore.
# Uses a throwaway Postgres and `rclone serve s3` on a private Docker network.
# Nothing is published on a host port. Not a production schedule.
# MinIO's registry path was not pullable here; rclone's S3 server is the stand-in.
set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)

die_logs() {
  printf 'smoke: %s\n' "$1" >&2
  docker logs "$pg" 2>&1 | tail -40 >&2 || true
  docker logs "$s3" 2>&1 | tail -40 >&2 || true
  exit 1
}

suffix=$$
net="wl-drill-${suffix}"
pg="wl-drill-pg-${suffix}"
s3="wl-s3-${suffix}"
probe="worklane-drill-ok"

cleanup() {
  local status=$?
  docker rm -f "$pg" "$s3" >/dev/null 2>&1 || true
  docker network rm "$net" >/dev/null 2>&1 || true
  if [[ -n "${workdir:-}" ]]; then
    rm -rf "$workdir"
  fi
  exit "$status"
}
trap cleanup EXIT

workdir=$(mktemp -d)
chmod 700 "$workdir"
access=$(openssl rand -hex 16)
secret=$(openssl rand -hex 16)
pgpass=$(openssl rand -hex 16)
age-keygen -o "$workdir/age.key" >/dev/null 2>&1
chmod 600 "$workdir/age.key"
recipient=$(age-keygen -y "$workdir/age.key")

docker network create "$net" >/dev/null
docker run -d --name "$s3" --network "$net" --network-alias wl-s3 \
  rclone/rclone:1.69.3 \
  serve s3 /data --addr 0.0.0.0:9000 --auth-key "${access},${secret}" >/dev/null
docker run -d --name "$pg" --network "$net" \
  -e POSTGRES_PASSWORD="$pgpass" \
  -e POSTGRES_USER=worklane \
  -e POSTGRES_DB=worklane \
  postgres:16-alpine >/dev/null

ready=0
for _ in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15 16 17 18 19 20 21 22 23 24 25 26 27 28 29 30; do
  if docker exec "$pg" pg_isready -U worklane -d worklane >/dev/null 2>&1; then
    ready=1
    break
  fi
  sleep 1
done
[[ "$ready" == 1 ]] || die_logs "postgres did not become ready"

mkdir_ok=0
for _ in 1 2 3 4 5 6 7 8 9 10; do
  if timeout 20 docker run --rm --network "$net" \
    -e RCLONE_CONFIG_WL_TYPE=s3 \
    -e RCLONE_CONFIG_WL_PROVIDER=Rclone \
    -e RCLONE_CONFIG_WL_ACCESS_KEY_ID="$access" \
    -e RCLONE_CONFIG_WL_SECRET_ACCESS_KEY="$secret" \
    -e RCLONE_CONFIG_WL_ENDPOINT=http://wl-s3:9000 \
    -e RCLONE_CONFIG_WL_REGION=auto \
    -e RCLONE_CONFIG_WL_FORCE_PATH_STYLE=true \
    rclone/rclone:1.69.3 \
    mkdir WL:wl-drill >/dev/null 2>&1; then
    mkdir_ok=1
    break
  fi
  sleep 1
done
[[ "$mkdir_ok" == 1 ]] || die_logs "s3 bucket was not created"

docker exec -e "PGPASSWORD=${pgpass}" "$pg" \
  psql -h 127.0.0.1 -U worklane -d worklane -v ON_ERROR_STOP=1 \
  -c "create table drill_probe(note text); insert into drill_probe(note) values ('${probe}');" \
  >/dev/null

export PGUSER=worklane
export PGDATABASE=worklane
export PGPASSWORD="$pgpass"
export PGPORT=5432
export BACKUP_DOCKER_CONTAINER="$pg"
export BACKUP_PROJECT=worklane-drill
export R2_ENDPOINT=http://wl-s3:9000
export R2_BUCKET=wl-drill
export R2_REGION=auto
export R2_PROVIDER=Rclone
export R2_DISABLE_MULTIPART=1
export R2_BACKUP_ACCESS_KEY_ID="$access"
export R2_BACKUP_SECRET_ACCESS_KEY="$secret"
export R2_RESTORE_ACCESS_KEY_ID="$access"
export R2_RESTORE_SECRET_ACCESS_KEY="$secret"
export BACKUP_DRILL=1
export BACKUP_AGE_RECIPIENT="$recipient"
export BACKUP_AGE_IDENTITY_FILE="$workdir/age.key"
export BACKUP_UPLOAD_NETWORK="$net"
export BACKUP_UPLOAD_IMAGE=rclone/rclone:1.69.3
unset WORKLANE_COMPOSE_FILE || true
unset BETTERSTACK_HEARTBEAT_URL || true

object=$("$SCRIPT_DIR/backup.sh" | tee "$workdir/backup.out" | awk -F= '/^BACKUP_OBJECT=/{print $2}')
[[ "$object" == *.dump.age ]] || {
  cat "$workdir/backup.out" >&2
  die_logs "backup did not report an age object"
}
printf 'smoke: uploaded %s\n' "$object"

docker exec -e "PGPASSWORD=${pgpass}" "$pg" \
  psql -h 127.0.0.1 -U worklane -d worklane -v ON_ERROR_STOP=1 \
  -c "drop table drill_probe;" >/dev/null

"$SCRIPT_DIR/restore.sh" "$object" --yes

found=$(docker exec -e "PGPASSWORD=${pgpass}" "$pg" \
  psql -h 127.0.0.1 -U worklane -d worklane -tA \
  -c "select note from drill_probe;")
[[ "$found" == "$probe" ]] || die_logs "restored row was '${found}'"
printf 'smoke: restored %s\n' "$found"
