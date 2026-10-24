#!/bin/sh
# Creates .env from .env.example with freshly generated random secrets. Refuses to overwrite an existing .env.
set -eu
root="$(cd "$(dirname "$0")/.." && pwd)"
target="$root/.env"
[ -e "$target" ] && { echo ".env already exists - not touching it."; exit 0; }

rand() { openssl rand -base64 48 | tr -dc 'A-Za-z0-9' | cut -c1-"$1"; }

sed \
  -e "s|^POSTGRES_PASSWORD=.*|POSTGRES_PASSWORD=$(rand 30)|" \
  -e "s|^SERVICE_DB_PASSWORD=.*|SERVICE_DB_PASSWORD=$(rand 30)|" \
  -e "s|^REDIS_PASSWORD=.*|REDIS_PASSWORD=$(rand 30)|" \
  -e "s|^JWT_SIGNING_KEY=.*|JWT_SIGNING_KEY=$(rand 48)|" \
  -e "s|^PAYMENTS_SECRET_KEY=.*|PAYMENTS_SECRET_KEY=$(openssl rand -base64 32)|" \
  -e "s|^SEED_ADMIN_PASSWORD=.*|SEED_ADMIN_PASSWORD=$(rand 14)aA1!|" \
  -e "s|^PGADMIN_PASSWORD=.*|PGADMIN_PASSWORD=$(rand 20)|" \
  "$root/.env.example" > "$target"
echo "Created $target. Seed admin: see SEED_ADMIN_EMAIL / SEED_ADMIN_PASSWORD in it."
