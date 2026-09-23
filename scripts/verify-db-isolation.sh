#!/bin/bash
# Proves the per-service database isolation that docker/postgres/init-dbs.sh sets up.
#
#   docker compose exec db bash /usr/local/bin/verify-db-isolation
#
# Exits non-zero if any check fails, so it can gate CI or a pre-commit hook.
# Requires CATALOG_DB_PASSWORD / STOCK_DB_PASSWORD / ORDERING_DB_PASSWORD in the
# environment, which docker-compose.yml supplies to the container.

set -uo pipefail

failures=0
checks=0

# try_connect <role> <password> <database> -> 0 if the connection is accepted
try_connect() {
  PGPASSWORD="$2" psql -h 127.0.0.1 -U "$1" -d "$3" -tAc 'SELECT 1' >/dev/null 2>&1
}

expect_allow() {
  checks=$((checks + 1))
  if try_connect "$1" "$2" "$3"; then
    echo "ok    $1 can connect to $3"
  else
    echo "FAIL  $1 cannot connect to $3 (expected success)"
    failures=$((failures + 1))
  fi
}

expect_deny() {
  checks=$((checks + 1))
  if try_connect "$1" "$2" "$3"; then
    echo "FAIL  $1 CAN connect to $3 (expected denial)"
    failures=$((failures + 1))
  else
    echo "ok    $1 cannot connect to $3"
  fi
}

expect_attr() {
  # expect_attr <label> <sql> <expected>
  checks=$((checks + 1))
  local actual
  actual=$(psql -U "$POSTGRES_USER" -d postgres -tAc "$2" 2>/dev/null | tr -d '[:space:]')
  if [ "$actual" = "$3" ]; then
    echo "ok    $1 = $actual"
  else
    echo "FAIL  $1 = '${actual:-<none>}' (expected '$3')"
    failures=$((failures + 1))
  fi
}

echo "=== 1. Each service role reaches its own database ==="
expect_allow catalog_svc  "$CATALOG_DB_PASSWORD"  agenticshop_catalog
expect_allow stock_svc    "$STOCK_DB_PASSWORD"    agenticshop_stock
expect_allow ordering_svc "$ORDERING_DB_PASSWORD" agenticshop_ordering

echo
echo "=== 2. No service role reaches another service's database ==="
expect_deny catalog_svc  "$CATALOG_DB_PASSWORD"  agenticshop_stock
expect_deny catalog_svc  "$CATALOG_DB_PASSWORD"  agenticshop_ordering
expect_deny stock_svc    "$STOCK_DB_PASSWORD"    agenticshop_catalog
expect_deny stock_svc    "$STOCK_DB_PASSWORD"    agenticshop_ordering
expect_deny ordering_svc "$ORDERING_DB_PASSWORD" agenticshop_catalog
expect_deny ordering_svc "$ORDERING_DB_PASSWORD" agenticshop_stock

echo
echo "=== 3. Service roles are not privileged ==="
for role in catalog_svc stock_svc ordering_svc; do
  expect_attr "$role.rolsuper"    "SELECT rolsuper    FROM pg_roles WHERE rolname='$role'" f
  expect_attr "$role.rolcreatedb" "SELECT rolcreatedb FROM pg_roles WHERE rolname='$role'" f
  expect_attr "$role.rolcreaterole" "SELECT rolcreaterole FROM pg_roles WHERE rolname='$role'" f
done

echo
echo "=== 4. Database ownership ==="
expect_attr "owner(agenticshop_catalog)" \
  "SELECT pg_catalog.pg_get_userbyid(datdba) FROM pg_database WHERE datname='agenticshop_catalog'" catalog_svc
expect_attr "owner(agenticshop_stock)" \
  "SELECT pg_catalog.pg_get_userbyid(datdba) FROM pg_database WHERE datname='agenticshop_stock'" stock_svc
expect_attr "owner(agenticshop_ordering)" \
  "SELECT pg_catalog.pg_get_userbyid(datdba) FROM pg_database WHERE datname='agenticshop_ordering'" ordering_svc

echo
echo "=== 5. The owning role can create tables (EF Core migrations need this) ==="
checks=$((checks + 1))
if PGPASSWORD="$CATALOG_DB_PASSWORD" psql -h 127.0.0.1 -U catalog_svc -d agenticshop_catalog \
     -v ON_ERROR_STOP=1 -c "CREATE TABLE IF NOT EXISTS _isolation_probe (id int); DROP TABLE _isolation_probe;" \
     >/dev/null 2>&1; then
  echo "ok    catalog_svc can create and drop a table in agenticshop_catalog"
else
  echo "FAIL  catalog_svc cannot create a table in agenticshop_catalog"
  failures=$((failures + 1))
fi

echo
echo "----------------------------------------------------------------"
if [ "$failures" -eq 0 ]; then
  echo "PASS  $checks/$checks checks succeeded — service databases are isolated."
  exit 0
fi

echo "FAIL  $failures of $checks checks failed."
exit 1
