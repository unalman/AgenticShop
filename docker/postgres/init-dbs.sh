#!/bin/bash
set -euo pipefail

# ---------------------------------------------------------------------------
# Per-service database isolation.
#
# POSTGRES_USER is a superuser that exists only to bootstrap: it creates the
# databases and the roles, then steps aside. Each service connects with its own
# non-superuser role, which OWNS exactly one database and has had CONNECT
# revoked for everyone else. The service boundary is therefore enforced by
# PostgreSQL rather than assumed by convention.
#
# Ownership is what keeps this simple. Since PostgreSQL 15 the `public` schema
# is owned by pg_database_owner, so the owning role can create tables — which
# is what EF Core migrations need — without any GRANT or ALTER DEFAULT
# PRIVILEGES plumbing.
#
# This script runs only when the data volume is empty. To re-apply it after a
# change, recreate the volume: `docker compose down -v`.
# ---------------------------------------------------------------------------

# database:role:password-env-var
services=(
  "agenticshop_catalog:catalog_svc:CATALOG_DB_PASSWORD"
  "agenticshop_stock:stock_svc:STOCK_DB_PASSWORD"
  "agenticshop_ordering:ordering_svc:ORDERING_DB_PASSWORD"
)

admin() {
  psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" -tAc "$1"
}

admin_in() {
  psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$1" -tAc "$2"
}

for entry in "${services[@]}"; do
  IFS=':' read -r database role password_var <<< "$entry"

  password="${!password_var:-}"
  if [ -z "$password" ]; then
    echo "FATAL: $password_var is not set" >&2
    exit 1
  fi

  # 1. Role. Cluster-wide, so it is created once and refreshed on re-run.
  if [ "$(admin "SELECT 1 FROM pg_roles WHERE rolname = '$role'")" = "1" ]; then
    admin "ALTER ROLE $role LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD '$password'"
    echo "role $role already existed, refreshed"
  else
    admin "CREATE ROLE $role LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD '$password'"
    echo "created role $role"
  fi

  # 2. Database. The entrypoint already created POSTGRES_DB owned by the
  #    bootstrap superuser, so ownership is asserted rather than assumed.
  if [ "$(admin "SELECT 1 FROM pg_database WHERE datname = '$database'")" = "1" ]; then
    admin "ALTER DATABASE $database OWNER TO $role"
    echo "database $database already existed, ownership set to $role"
  else
    admin "CREATE DATABASE $database OWNER $role"
    echo "created database $database owned by $role"
  fi

  # 3. Deny every other role, then admit only the owner. Without the REVOKE,
  #    PUBLIC holds CONNECT on every database by default.
  admin "REVOKE ALL ON DATABASE $database FROM PUBLIC"
  admin "GRANT CONNECT, TEMP ON DATABASE $database TO $role"

  # 4. The owner must control the public schema so migrations can create tables.
  admin_in "$database" "ALTER SCHEMA public OWNER TO $role"

  echo "  -> $role may reach $database only"
done

echo
echo "Isolation summary:"
admin "SELECT rolname, rolsuper, rolcreatedb, rolcreaterole FROM pg_roles
       WHERE rolname IN ('catalog_svc','stock_svc','ordering_svc')
       ORDER BY rolname"
admin "SELECT datname, pg_catalog.pg_get_userbyid(datdba) AS owner
       FROM pg_database WHERE datname LIKE 'agenticshop_%' ORDER BY datname"

echo
echo "Run 'docker compose exec db bash /usr/local/bin/verify-db-isolation' to prove it."
