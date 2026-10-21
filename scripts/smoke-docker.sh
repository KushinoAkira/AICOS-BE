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

json_field() { grep -o "\"$1\":\"[^\"]*\"" | head -1 | sed "s/^\"$1\":\"//; s/\"$//"; }
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


# ------------------------------------------------------------------ orders, bar, hand-over, exceptions, reports
echo
echo "== orders"
wait_until() { # description, command that prints the value, expected value
  i=0
  while [ "$i" -lt 25 ]; do
    value=$(eval "$2")
    [ "$value" = "$3" ] && { check "$1" "$3" "$value"; return; }
    i=$((i + 1))
    sleep 1
  done
  check "$1" "$3" "$value"
}
field_of() { json_field "$1"; }

r=$(call POST /api/v1/identity/users "$BRAND" "{\"fullName\":\"Pha Che\",\"email\":\"barista-$STAMP@example.com\",\"username\":\"barista$STAMP\",\"password\":\"$BRAND_PASSWORD\",\"role\":\"Barista\",\"branchId\":\"$BRANCH\"}")
check "barista account created" 201 "$(status_of "$r")"
r=$(call POST /api/v1/identity/users "$BRAND" "{\"fullName\":\"Quan Ly\",\"email\":\"manager-$STAMP@example.com\",\"username\":\"manager$STAMP\",\"password\":\"$BRAND_PASSWORD\",\"role\":\"BranchManager\",\"branchId\":\"$BRANCH\"}")
check "branch manager account created" 201 "$(status_of "$r")"
BARISTA=$(call POST /api/v1/identity/auth/login "" "{\"login\":\"barista-$STAMP@example.com\",\"password\":\"$BRAND_PASSWORD\"}" | sed 's/ [0-9]*$//' | json_field accessToken)
MANAGER=$(call POST /api/v1/identity/auth/login "" "{\"login\":\"manager-$STAMP@example.com\",\"password\":\"$BRAND_PASSWORD\"}" | sed 's/ [0-9]*$//' | json_field accessToken)

r=$(call POST /api/v1/menu/categories "$BRAND" '{"name":"Ca phe"}')
check "menu category created" 201 "$(status_of "$r")"
CATEGORY=$(body_of "$r" | json_field id)
r=$(call POST /api/v1/menu/products "$BRAND" "{\"categoryId\":\"$CATEGORY\",\"name\":\"Latte\",\"basePrice\":35000}")
check "product created" 201 "$(status_of "$r")"
PRODUCT=$(body_of "$r" | json_field id)
r=$(call PUT "/api/v1/menu/products/$PRODUCT/branches/$BRANCH" "$BRAND" '{"isPublished":true}')
check "product published to the branch" 200 "$(status_of "$r")"

r=$(call POST /api/v1/orders "$CASHIER" "{\"type\":\"Takeaway\",\"items\":[{\"productId\":\"$PRODUCT\",\"quantity\":2,\"unitPrice\":1}]}")
check "counter order created (the price comes from the menu)" 201 "$(status_of "$r")"
ORDER=$(body_of "$r" | json_field id)
case "$(body_of "$r")" in *'"finalAmount":70000'*) check "order total is 2 x 35000" yes yes ;; *) check "order total is 2 x 35000" yes no ;; esac

r=$(call POST /api/v1/payments/transactions/cash "$CASHIER" "{\"orderId\":\"$ORDER\",\"amount\":1}")
check "paying the wrong amount is refused" 422 "$(status_of "$r")"
r=$(call POST /api/v1/payments/transactions/cash "$CASHIER" "{\"orderId\":\"$ORDER\",\"amount\":70000,\"amountTendered\":100000}")
check "cash recorded for the order" 201 "$(status_of "$r")"
wait_until "the order becomes Paid (payment event)" "call GET /api/v1/orders/$ORDER/payment \"\$CASHIER\" | sed 's/ [0-9]*\$//' | field_of paymentStatus" Paid

wait_until "the bar queue shows the paid takeaway order" "call GET /api/v1/kitchen/queue \"\$BARISTA\" | grep -c $ORDER" 1
TICKET=$(call GET /api/v1/kitchen/queue "$BARISTA" | sed 's/ [0-9]*$//' | json_field id)
r=$(call PUT "/api/v1/kitchen/tickets/$TICKET/status" "$BARISTA" '{"status":"Brewing"}')
check "barista starts brewing" 200 "$(status_of "$r")"
r=$(call PUT "/api/v1/kitchen/tickets/$TICKET/status" "$BARISTA" '{"status":"ReadyForPickup"}')
check "barista marks the order ready" 200 "$(status_of "$r")"
wait_until "the order becomes Ready (kitchen event)" "call GET /api/v1/orders/$ORDER \"\$CASHIER\" | sed 's/ [0-9]*\$//' | field_of status" Ready
r=$(call POST "/api/v1/orders/$ORDER/deliver" "$CASHIER" '{}')
check "cashier hands the order over" 200 "$(status_of "$r")"
wait_until "the order is Completed" "call GET /api/v1/orders/$ORDER \"\$CASHIER\" | sed 's/ [0-9]*\$//' | field_of status" Completed

r=$(call GET "/api/v1/orders/$ORDER/receipt" "$CASHIER")
check "bill printed" 200 "$(status_of "$r")"

r=$(call POST /api/v1/orders "$CASHIER" "{\"type\":\"DineIn\",\"tableNumber\":\"T1\",\"items\":[{\"productId\":\"$PRODUCT\",\"quantity\":1}]}")
ORDER2=$(body_of "$r" | json_field id)
r=$(call POST /api/v1/payments/transactions "$CASHIER" "{\"orderId\":\"$ORDER2\",\"amount\":35000,\"providerCode\":\"Mock\"}")
check "online payment started for a second order" 201 "$(status_of "$r")"
PAY2=$(body_of "$r" | json_field id)
r=$(call POST "/api/v1/payments/transactions/$PAY2/simulate" "$CASHIER" '{"outcome":"success"}')
if [ "$(status_of "$r")" = "403" ]; then
  echo "SKIP  provider simulation is disabled here (set PAYMENTS_ALLOW_SIMULATION=true in .env for a demo)"
else
  check "provider confirms the online payment" 200 "$(status_of "$r")"
  wait_until "the second order becomes Paid" "call GET /api/v1/orders/$ORDER2/payment \"\$CASHIER\" | sed 's/ [0-9]*\$//' | field_of paymentStatus" Paid
fi

r=$(call POST "/api/v1/orders/$ORDER/exception" "$MANAGER" '{"reason":"Customer complaint","refundKind":"Cancellation"}')
check "manager cancels a paid order as an exception" 200 "$(status_of "$r")"
wait_until "the exception refund is paid back" "call GET /api/v1/orders/$ORDER \"\$MANAGER\" | sed 's/ [0-9]*\$//' | field_of refundStatus" Refunded
r=$(call GET /api/v1/orders/exceptions "$MANAGER")
check "exceptions list" 200 "$(status_of "$r")"

r=$(call GET "/api/v1/payments/reconciliation/auto" "$MANAGER")
check "automatic reconciliation with the Order service" 200 "$(status_of "$r")"
r=$(call GET "/api/v1/reporting/chain" "$BRAND")
check "chain report" 200 "$(status_of "$r")"
wait_until "the chain report counts the orders" "call GET /api/v1/reporting/chain \"\$BRAND\" | sed 's/ [0-9]*\$//' | grep -o '\"orders\":[0-9]*' | head -1" '"orders":2'
r=$(call GET "/api/v1/reporting/branch?branchId=$BRANCH" "$BRAND")
check "branch report" 200 "$(status_of "$r")"

r=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$GATEWAY/hubs/kitchen/negotiate?negotiateVersion=1&access_token=$BARISTA")
check "SignalR hub negotiates through the gateway with the token" 200 "$r"
r=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$GATEWAY/hubs/kitchen/negotiate?negotiateVersion=1")
check "SignalR hub refuses an anonymous client" 401 "$r"
echo
if [ "$failed" -eq 0 ]; then echo "ALL CHECKS PASSED"; else echo "$failed CHECK(S) FAILED"; exit 1; fi
