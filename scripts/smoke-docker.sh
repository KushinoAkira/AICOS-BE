#!/bin/sh
# End-to-end smoke test against the RUNNING Docker stack, through the gateway.
#   GATEWAY=http://localhost:8080 ./scripts/smoke-docker.sh
# Reads SEED_ADMIN_EMAIL / SEED_ADMIN_PASSWORD from .env (never prints them). Creates a throw-away brand named smoke-<time>.
set -u
root="$(cd "$(dirname "$0")/.." && pwd)"
GATEWAY="${GATEWAY:-http://localhost:8080}"
env_value() { sed -n "s/^$1=//p" "$root/.env" | head -1; }
ADMIN_EMAIL="${SEED_ADMIN_EMAIL:-$(env_value SEED_ADMIN_EMAIL)}"
ADMIN_PASSWORD="${SEED_ADMIN_PASSWORD:-$(env_value SEED_ADMIN_PASSWORD)}"
STAMP="$(date +%s)"
BRAND_PASSWORD="Smoke-$(date +%N)aA1!"
failed=0

json_field() { sed -n "s/.*\"$1\":\"\\([^\"]*\\)\".*/\\1/p" | head -1; }
call() { # method path token [body] -> prints "<status> <body>"
  method="$1"; path="$2"; token="$3"; body="${4:-}"
  if [ -n "$body" ]; then
    curl -s -w ' %{http_code}' -X "$method" "$GATEWAY$path" -H "Content-Type: application/json" ${token:+-H "Authorization: Bearer $token"} -d "$body"
  else
    curl -s -w ' %{http_code}' -X "$method" "$GATEWAY$path" ${token:+-H "Authorization: Bearer $token"}
  fi
}
status_of() { echo "$1" | awk '{print $NF}'; }
body_of() { echo "$1" | sed 's/ [0-9]*$//'; }
check() { # name expected actual
  if [ "$2" = "$3" ]; then echo "PASS  $1"; else echo "FAIL  $1 (expected $2, got $3)"; failed=$((failed + 1)); fi
}

echo "== gateway $GATEWAY"
r=$(call POST /api/v1/identity/auth/login "" "{\"login\":\"$ADMIN_EMAIL\",\"password\":\"$ADMIN_PASSWORD\"}")
check "super admin signs in" 200 "$(status_of "$r")"
SUPER=$(body_of "$r" | json_field accessToken)

r=$(call POST /api/v1/identity/tenants "$SUPER" "{\"name\":\"Smoke $STAMP\",\"code\":\"smoke-$STAMP\",\"firstAdmin\":{\"fullName\":\"Smoke Owner\",\"email\":\"owner-$STAMP@example.com\",\"password\":\"$BRAND_PASSWORD\"}}")
check "tenant created with its first brand admin" 201 "$(status_of "$r")"

r=$(call POST /api/v1/identity/auth/login "" "{\"login\":\"owner-$STAMP@example.com\",\"password\":\"$BRAND_PASSWORD\"}")
check "brand admin signs in" 200 "$(status_of "$r")"
BRAND=$(body_of "$r" | json_field accessToken)

r=$(call POST /api/v1/identity/branches "$BRAND" '{"name":"Quan 1","address":"1 Le Loi","phone":"0900000001","openingTime":"07:00:00","closingTime":"22:00:00"}')
check "branch created" 201 "$(status_of "$r")"
BRANCH=$(body_of "$r" | json_field id)

r=$(call POST /api/v1/identity/users "$BRAND" "{\"fullName\":\"Thu Ngan\",\"email\":\"cashier-$STAMP@example.com\",\"username\":\"cashier$STAMP\",\"password\":\"$BRAND_PASSWORD\",\"role\":\"Cashier\",\"branchId\":\"$BRANCH\"}")
check "cashier account created" 201 "$(status_of "$r")"
r=$(call POST /api/v1/identity/auth/login "" "{\"login\":\"cashier-$STAMP@example.com\",\"password\":\"$BRAND_PASSWORD\"}")
CASHIER=$(body_of "$r" | json_field accessToken)

r=$(call GET /api/v1/payments/providers/enabled "$CASHIER")
check "the Mock payment provider is enabled" 200 "$(status_of "$r")"
case "$(body_of "$r")" in *Mock*) check "Mock listed" yes yes ;; *) check "Mock listed" yes no ;; esac

ORDER="00000000-0000-4000-8000-$(printf '%012d' "$STAMP")"
r=$(call POST /api/v1/payments/transactions "$CASHIER" "{\"orderId\":\"$ORDER\",\"amount\":85000,\"providerCode\":\"Mock\"}")
check "online payment started" 201 "$(status_of "$r")"
PAYMENT=$(body_of "$r" | json_field id)

r=$(call POST "/api/v1/payments/transactions/$PAYMENT/simulate" "$CASHIER" '{"outcome":"success"}')
SIM=$(status_of "$r")
if [ "$SIM" = "403" ]; then
  echo "SKIP  provider simulation is disabled here (set PAYMENTS_ALLOW_SIMULATION=true in .env for a demo); refund check skipped"
else
  check "provider confirms the payment" 200 "$SIM"
  case "$(body_of "$r")" in *'"status":"Succeeded"'*) check "payment is Succeeded" yes yes ;; *) check "payment is Succeeded" yes no ;; esac
fi

r=$(call POST "/api/v1/payments/transactions/cash" "$CASHIER" "{\"orderId\":\"00000000-0000-4000-8000-$(printf '%012d' $((STAMP + 1)))\",\"amount\":50000,\"amountTendered\":100000}")
check "cash payment recorded" 201 "$(status_of "$r")"

if [ "$SIM" != "403" ]; then
  r=$(call POST "/api/v1/payments/transactions/$PAYMENT/refunds" "$CASHIER" '{"kind":"Cancellation","reason":"smoke test"}')
  check "refund inside the policy is paid back" 201 "$(status_of "$r")"
fi

r=$(call GET "/api/v1/payments/reconciliation?branchId=$BRANCH" "$BRAND")
check "reconciliation report" 200 "$(status_of "$r")"

r=$(call PUT /api/v1/workforce/pay-rules "$BRAND" '{"fullTimeDayRate":300000,"partTimeHourRate":30000,"replacementHourRate":45000,"overtimeAfterHours":8,"overtimeMultiplier":1.5}')
check "pay rules saved" 200 "$(status_of "$r")"
today=$(date +%Y-%m-%d)
r=$(call GET "/api/v1/workforce/payroll/summary?branchId=$BRANCH&from=$today&to=$today" "$BRAND")
check "payroll summary" 200 "$(status_of "$r")"

r=$(call GET /api/v1/identity/audit-logs "$BRAND")
check "brand admin reads the audit history" 200 "$(status_of "$r")"

r=$(call GET /api/v1/platform/health "$SUPER")
check "platform health (super admin)" 200 "$(status_of "$r")"

echo
if [ "$failed" -eq 0 ]; then echo "ALL CHECKS PASSED"; else echo "$failed CHECK(S) FAILED"; exit 1; fi
