#!/usr/bin/env bash
# Hermetic script regression: fake curl/sleep only. No Docker, network, database or real credentials.
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
test_dir="$(mktemp -d)"
cleanup() {
  rm -f -- "$test_dir/bin/curl" "$test_dir/bin/sleep" "$test_dir/scripts/smoke-docker.sh" \
    "$test_dir/calls" "$test_dir/branches" "$test_dir/logins" "$test_dir/output"
  rmdir -- "$test_dir/bin" "$test_dir/scripts" "$test_dir"
}
trap cleanup EXIT
mkdir "$test_dir/bin" "$test_dir/scripts"
cp "$root/scripts/smoke-docker.sh" "$test_dir/scripts/smoke-docker.sh"

cat > "$test_dir/bin/sleep" <<'MOCK'
#!/bin/sh
exit 0
MOCK
cat > "$test_dir/bin/curl" <<'MOCK'
#!/bin/sh
set -eu
method=GET
url=''
while [ "$#" -gt 0 ]; do
  case "$1" in
    -X) method="$2"; shift 2 ;;
    http://smoke.invalid*) url="$1"; shift ;;
    *) shift ;;
  esac
done
[ -n "$url" ] || exit 9
path="${url#http://smoke.invalid}"
# Log paths only: no request bodies, passwords, tokens or authorization headers.
printf '%s %s\n' "$method" "$path" >> "$SMOKE_TEST_ROOT/calls"
id='11111111-1111-4111-8111-111111111111'
body='{}'
status=200
case "$method:$path" in
  GET:/api/v1/identity/public/subscription-plans) printf '200'; exit 0 ;;
  POST:/api/v1/identity/auth/login)
    count=0; [ ! -f "$SMOKE_TEST_ROOT/logins" ] || count="$(cat "$SMOKE_TEST_ROOT/logins")"
    count=$((count + 1)); printf '%s' "$count" > "$SMOKE_TEST_ROOT/logins"
    if [ "$count" -eq 1 ] && [ "$SMOKE_TEST_CASE" = login-retry ]; then
      status=429
    elif [ "$SMOKE_TEST_CASE" = login-failure ]; then
      status=500; body='{"detail":"DO_NOT_LOG_RESPONSE_BODY"}'
    elif [ "$SMOKE_TEST_CASE" = missing-token ]; then
      body='{"detail":"DO_NOT_LOG_RESPONSE_BODY"}'
    else
      body='{"accessToken":"UNIT_TEST_TOKEN_NOT_REAL"}'
    fi ;;
  GET:/api/v1/identity/subscription) status=404 ;;
  POST:/api/v1/identity/branches)
    if [ ! -f "$SMOKE_TEST_ROOT/branches" ]; then
      printf '1' > "$SMOKE_TEST_ROOT/branches"; status=422
    else
      status=201; body="{\"id\":\"$id\"}"
    fi ;;
  GET:/api/v1/identity/tenants/*) body='{"branchCount":1}' ;;
  POST:/api/v1/identity/tenants|POST:/api/v1/identity/subscription-plans|POST:/api/v1/identity/users|POST:/api/v1/identity/tenants/*/subscription|POST:/api/v1/menu/categories|POST:/api/v1/menu/products)
    status=201; body="{\"id\":\"$id\"}" ;;
  GET:/api/v1/payments/providers/enabled) body='[{"code":"Mock"}]' ;;
  POST:/api/v1/orders)
    case "$SMOKE_TEST_CASE" in
      missing-id) status=201; body='{"finalAmount":70000}' ;;
      invalid-id) status=201; body='{"id":"not-a-guid","finalAmount":70000}' ;;
      valid-id) status=201; body="{\"id\":\"$id\",\"finalAmount\":70000}" ;;
      multi-line-error) status=500; body='{
        "detail": "DO_NOT_LOG_RESPONSE_BODY"
      }' ;;
      *) status=500; body='{"detail":"DO_NOT_LOG_RESPONSE_BODY"}' ;;
    esac ;;
  POST:/api/v1/payments/transactions/cash) status=500 ;; # Deliberate boundary after a valid Order.
esac
printf '%s %s' "$body" "$status"
MOCK
chmod +x "$test_dir/bin/curl" "$test_dir/bin/sleep"

for scenario in order-500 multi-line-error missing-id invalid-id valid-id login-retry login-failure missing-token; do
  rm -f -- "$test_dir/calls" "$test_dir/branches" "$test_dir/logins" "$test_dir/output"
  result=0
  SMOKE_TEST_CASE="$scenario" SMOKE_TEST_ROOT="$test_dir" \
    SEED_ADMIN_EMAIL='seed@example.invalid' SEED_ADMIN_PASSWORD='DUMMY_TEST_PASSWORD' \
    GATEWAY='http://smoke.invalid' PATH="$test_dir/bin:$PATH" \
    bash "$test_dir/scripts/smoke-docker.sh" > "$test_dir/output" 2>&1 || result=$?
  [ "$result" -eq 1 ] || { echo "FAIL $scenario: expected a controlled abort, got $result"; exit 1; }
  if grep -Eq 'Usage: grep|DO_NOT_LOG_RESPONSE_BODY|UNIT_TEST_TOKEN_NOT_REAL|DUMMY_TEST_PASSWORD' "$test_dir/output"; then
    echo "FAIL $scenario: malformed grep or sensitive output"; exit 1
  fi
  case "$scenario" in
    order-500|multi-line-error|login-retry)
      grep -Fq 'FAIL  counter order created (the price comes from the menu) (expected 201, got 500)' "$test_dir/output" ;;
    missing-id|invalid-id)
      grep -Fq 'ABORT  counter order creation did not return a valid ID.' "$test_dir/output" ;;
    valid-id)
      grep -Fq 'PASS  order total is 2 x 35000' "$test_dir/output"
      grep -Fq 'POST /api/v1/payments/transactions/cash' "$test_dir/calls" ;;
    login-failure)
      grep -Fq 'FAIL  super admin signs in (expected 200, got 500)' "$test_dir/output" ;;
    missing-token)
      grep -Fq 'ABORT  no Super Admin token was returned' "$test_dir/output" ;;
  esac
  if [ "$scenario" != valid-id ] && grep -Fq 'POST /api/v1/payments/transactions/cash' "$test_dir/calls"; then
    echo "FAIL $scenario: dependent payment request ran after a failed prerequisite"; exit 1
  fi
  if [ "$scenario" = login-retry ]; then
    grep -Fq 'PASS  super admin signs in' "$test_dir/output"
    grep -Fq 'login answered 429' "$test_dir/output"
  fi
  echo "PASS $scenario (mock HTTP only)"
done
echo '8 script regression scenarios passed; real Compose E2E was not run.'
