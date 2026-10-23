#!/bin/bash
# Runs once, on first start of an empty PostgreSQL volume (docker-entrypoint-initdb.d).
#
# Implements "one shared instance, schema-per-service" from the architecture:
#   - every service owns exactly one schema and connects with its own login role
#   - a role can only see its own schema => no cross-service SQL joins are even possible
#
# DEV-ONLY credentials: all service roles share SERVICE_DB_PASSWORD. Use per-service secrets in real deployments.
set -euo pipefail

: "${SERVICE_DB_PASSWORD:?SERVICE_DB_PASSWORD must be set}"

# Keep in sync with AicosServiceInfo.Schema in each service (and the ai service for the Python side).
SCHEMAS="identity menu table_reservation ordering payment kitchen workforce reporting customer ai"

for schema in $SCHEMAS; do
  echo ">> creating schema '${schema}' owned by role '${schema}_svc'"
  psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<-EOSQL
    CREATE ROLE ${schema}_svc LOGIN PASSWORD '${SERVICE_DB_PASSWORD}';
    CREATE SCHEMA ${schema} AUTHORIZATION ${schema}_svc;
    ALTER ROLE ${schema}_svc SET search_path = ${schema};
EOSQL
done

# Nobody gets to create objects in 'public'.
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<-EOSQL
  REVOKE CREATE ON SCHEMA public FROM PUBLIC;
EOSQL

# pgvector lives inside the AI service's schema (RAG embeddings).
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<-EOSQL
  CREATE EXTENSION IF NOT EXISTS vector WITH SCHEMA ai;
EOSQL

echo ">> AICOS database initialised"
