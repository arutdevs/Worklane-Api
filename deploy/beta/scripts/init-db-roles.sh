#!/usr/bin/env bash
# Create migrator / app / readonly roles. Idempotent. Passwords come from the env file.
# The migrator role owns objects it creates. There is no worklane_owner role:
# nothing called SET ROLE, so a NOLOGIN group never became the owner.
# Databases created by the old script still have that role. Revoke membership,
# then DROP ROLE IF EXISTS. The app role can read __EFMigrationsHistory and
# cannot insert, update, or delete it. The migrator owns the table, so migrate
# still writes the history row. The revoke is skipped until the table exists,
# which is why ef-migrate.sh calls this script again after migrate.
#   init-db-roles.sh /etc/worklane/<slot>.env
set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
# shellcheck source=lib.sh
source "$SCRIPT_DIR/lib.sh"

ENV_FILE=$(resolve_env_file "${1:-}")
"$SCRIPT_DIR/preflight.sh" --up "$ENV_FILE"

db=$(require_ident POSTGRES_DB)
admin=$(require_ident POSTGRES_USER)
app=$(require_ident POSTGRES_APP_USER)
migrator=$(require_ident POSTGRES_MIGRATOR_USER)
readonly_role=$(require_ident POSTGRES_READONLY_USER)
app_pw=$(require_env POSTGRES_APP_PASSWORD)
migrator_pw=$(require_env POSTGRES_MIGRATOR_PASSWORD)
readonly_pw=$(require_env POSTGRES_READONLY_PASSWORD)

sql_quote() {
  printf '%s' "$1" | sed "s/'/''/g"
}

compose up -d --wait postgres

umask 077
sql=$(mktemp)
trap 'rm -f "$sql"' EXIT
cat >"$sql" <<SQL
DO \$do\$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '$(sql_quote "$migrator")') THEN
    CREATE ROLE $(printf '%s' "$migrator") LOGIN PASSWORD '$(sql_quote "$migrator_pw")';
  ELSE
    ALTER ROLE $(printf '%s' "$migrator") WITH LOGIN PASSWORD '$(sql_quote "$migrator_pw")';
  END IF;
END
\$do\$;

DO \$do\$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '$(sql_quote "$app")') THEN
    CREATE ROLE $(printf '%s' "$app") LOGIN PASSWORD '$(sql_quote "$app_pw")';
  ELSE
    ALTER ROLE $(printf '%s' "$app") WITH LOGIN PASSWORD '$(sql_quote "$app_pw")';
  END IF;
END
\$do\$;

DO \$do\$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '$(sql_quote "$readonly_role")') THEN
    CREATE ROLE $(printf '%s' "$readonly_role") LOGIN PASSWORD '$(sql_quote "$readonly_pw")';
  ELSE
    ALTER ROLE $(printf '%s' "$readonly_role") WITH LOGIN PASSWORD '$(sql_quote "$readonly_pw")';
  END IF;
END
\$do\$;

GRANT CONNECT ON DATABASE ${db} TO ${migrator}, ${app}, ${readonly_role};
GRANT CREATE, USAGE ON SCHEMA public TO ${migrator};
GRANT USAGE ON SCHEMA public TO ${app}, ${readonly_role};

ALTER DEFAULT PRIVILEGES FOR ROLE ${migrator} IN SCHEMA public
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ${app};
ALTER DEFAULT PRIVILEGES FOR ROLE ${migrator} IN SCHEMA public
  GRANT SELECT ON TABLES TO ${readonly_role};
ALTER DEFAULT PRIVILEGES FOR ROLE ${migrator} IN SCHEMA public
  GRANT USAGE, SELECT ON SEQUENCES TO ${app};
ALTER DEFAULT PRIVILEGES FOR ROLE ${migrator} IN SCHEMA public
  GRANT SELECT ON SEQUENCES TO ${readonly_role};

GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO ${app};
GRANT SELECT ON ALL TABLES IN SCHEMA public TO ${readonly_role};
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO ${app};
GRANT SELECT ON ALL SEQUENCES IN SCHEMA public TO ${readonly_role};

-- Old init script: CREATE ROLE worklane_owner NOLOGIN; GRANT worklane_owner TO the migrator.
-- Membership has to be revoked or DROP ROLE fails. REASSIGN/DROP OWNED cover a
-- database where that role was given objects. Both are no-ops when it owns nothing.
DO \$do\$
DECLARE
  member text;
BEGIN
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'worklane_owner') THEN
    FOR member IN
      SELECT m.rolname
      FROM pg_auth_members am
      JOIN pg_roles granted ON granted.oid = am.roleid
      JOIN pg_roles m ON m.oid = am.member
      WHERE granted.rolname = 'worklane_owner'
    LOOP
      EXECUTE format('REVOKE worklane_owner FROM %I', member);
    END LOOP;
  END IF;
END
\$do\$;

SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'worklane_owner') AS drop_owner \gset
\if :drop_owner
REASSIGN OWNED BY worklane_owner TO ${migrator};
DROP OWNED BY worklane_owner;
\endif
DROP ROLE IF EXISTS worklane_owner;

-- After Migrate() the history table exists and default privileges have already
-- granted the app role DML. Take that back. SELECT stays.
DO \$do\$
BEGIN
  IF to_regclass('public."__EFMigrationsHistory"') IS NOT NULL THEN
    REVOKE INSERT, UPDATE, DELETE ON TABLE public."__EFMigrationsHistory" FROM ${app};
  END IF;
END
\$do\$;
SQL

compose exec -T postgres psql -q -v ON_ERROR_STOP=1 -U "$admin" -d "$db" <"$sql"
printf 'roles ready in %s\n' "$(require_env COMPOSE_PROJECT_NAME)"
