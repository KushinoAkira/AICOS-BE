#!/bin/bash
# Runs once, on first start of an empty PostgreSQL volume (docker-entrypoint-initdb.d).
#
# Implements "database-per-service on one PostgreSQL instance" from the architecture:
#   - every service owns exactly one logical database named ${POSTGRES_DB}_<service> (aicos_identity, aicos_ordering, ...)
#   - inside it the service owns one schema with the same name as before, so entities, migrations and search_path do not change
#   - the service connects with its own login role, which is the owner of its database and may connect to no other one
#   => no cross-service SQL join or foreign key is possible; services exchange data only through APIs and events.
#
# The instance (container, port, backup job) is shared on purpose to keep the demo light. Moving a database to its own server later
# only means changing that service's connection string.
#
# DEV-ONLY credentials: all service roles share SERVICE_DB_PASSWORD. Use per-service secrets in real deployments.
set -euo pipefail

: "${SERVICE_DB_PASSWORD:?SERVICE_DB_PASSWORD must be set}"

# Keep in sync with AicosServiceInfo.Schema in each service (and the ai service for the Python side).
SERVICES="identity menu table_reservation ordering payment kitchen workforce reporting customer ai"

psql_admin() { # <database> : reads SQL from stdin
  psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$1"
}

for svc in $SERVICES; do
  db="${POSTGRES_DB}_${svc}"
  role="${svc}_svc"
  echo ">> creating database '${db}' owned by role '${role}'"

  psql_admin "$POSTGRES_DB" <<-EOSQL
    CREATE ROLE ${role} LOGIN PASSWORD '${SERVICE_DB_PASSWORD}';
    CREATE DATABASE ${db} OWNER ${role};
    REVOKE ALL ON DATABASE ${db} FROM PUBLIC;
    GRANT CONNECT ON DATABASE ${db} TO ${role};
EOSQL

  psql_admin "$db" <<-EOSQL
    CREATE SCHEMA ${svc} AUTHORIZATION ${role};
    ALTER ROLE ${role} SET search_path = ${svc};
    REVOKE CREATE ON SCHEMA public FROM PUBLIC;
EOSQL
done

# pgvector lives inside the AI service's own database and schema (RAG embeddings). Creating an extension needs the superuser.
psql_admin "${POSTGRES_DB}_ai" <<-EOSQL
  CREATE EXTENSION IF NOT EXISTS vector WITH SCHEMA ai;
EOSQL

echo ">> AICOS databases initialised"
