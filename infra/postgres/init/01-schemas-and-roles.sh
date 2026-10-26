#!/bin/bash
# Runs once, on first start of an empty PostgreSQL volume (docker-entrypoint-initdb.d).
#
# Creates runtime login roles for the single aicos_db/public deployment model.
# Table ownership is enforced by reviewed grants, not by per-service schemas.
#
# Credentials are injected through environment variables. Never commit real values.
set -euo pipefail

: "${IDENTITY_DB_PASSWORD:?IDENTITY_DB_PASSWORD must be set}"
: "${MENU_DB_PASSWORD:?MENU_DB_PASSWORD must be set}"
: "${TABLE_DB_PASSWORD:?TABLE_DB_PASSWORD must be set}"
: "${ORDERING_DB_PASSWORD:?ORDERING_DB_PASSWORD must be set}"
: "${PAYMENT_DB_PASSWORD:?PAYMENT_DB_PASSWORD must be set}"
: "${INVENTORY_DB_PASSWORD:?INVENTORY_DB_PASSWORD must be set}"
: "${KITCHEN_DB_PASSWORD:?KITCHEN_DB_PASSWORD must be set}"
: "${WORKFORCE_DB_PASSWORD:?WORKFORCE_DB_PASSWORD must be set}"
: "${REPORTING_DB_PASSWORD:?REPORTING_DB_PASSWORD must be set}"
: "${CUSTOMER_DB_PASSWORD:?CUSTOMER_DB_PASSWORD must be set}"
: "${AI_DB_PASSWORD:?AI_DB_PASSWORD must be set}"

create_runtime_role() {
  local role_name="$1"
  local role_password="$2"

  echo ">> creating runtime role '${role_name}'"
  psql -v ON_ERROR_STOP=1 \
    --username "$POSTGRES_USER" \
    --dbname "$POSTGRES_DB" \
    --set=role_name="$role_name" \
    --set=role_password="$role_password" <<-'EOSQL'
    CREATE ROLE :"role_name" LOGIN PASSWORD :'role_password';
    ALTER ROLE :"role_name" SET search_path = public;
EOSQL
}

create_runtime_role identity_svc "$IDENTITY_DB_PASSWORD"
create_runtime_role menu_svc "$MENU_DB_PASSWORD"
create_runtime_role table_reservation_svc "$TABLE_DB_PASSWORD"
create_runtime_role ordering_svc "$ORDERING_DB_PASSWORD"
create_runtime_role payment_svc "$PAYMENT_DB_PASSWORD"
create_runtime_role inventory_svc "$INVENTORY_DB_PASSWORD"
create_runtime_role kitchen_svc "$KITCHEN_DB_PASSWORD"
create_runtime_role workforce_svc "$WORKFORCE_DB_PASSWORD"
create_runtime_role reporting_svc "$REPORTING_DB_PASSWORD"
create_runtime_role customer_svc "$CUSTOMER_DB_PASSWORD"
create_runtime_role ai_svc "$AI_DB_PASSWORD"

# Grants are intentionally not auto-applied. Review and explicitly execute
# infra/postgres/security/public-service-grants.sql after the schema exists.

echo ">> AICOS database initialised"
