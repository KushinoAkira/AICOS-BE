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
TENANT=$(body_of "$r" | json_field id)

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

# ------------------------------------------------------------------ subscription (SCRUM-132) and loyalty (SCRUM-133, 134)
echo
echo "== subscription and loyalty"
r=$(call GET /api/v1/identity/subscription "$BRAND")
check "a brand without a plan has no subscription yet" 404 "$(status_of "$r")"
r=$(call POST /api/v1/identity/subscription-plans "$SUPER" "{\"name\":\"Smoke $STAMP\",\"tier\":\"Basic\",\"fee\":500000,\"billingPeriod\":\"Monthly\",\"maxBranches\":5,\"maxStaffAccounts\":50}")
check "plan created" 201 "$(status_of "$r")"
PLAN=$(body_of "$r" | json_field id)
r=$(call POST "/api/v1/identity/tenants/$TENANT/subscription" "$SUPER" "{\"planId\":\"$PLAN\"}")
check "plan assigned to the brand" 201 "$(status_of "$r")"
r=$(call GET /api/v1/identity/subscription "$BRAND")
check "brand admin sees the subscription" 200 "$(status_of "$r")"
r=$(call POST /api/v1/identity/subscription/renew "$BRAND" '{}')
check "simulated payment of the period" 200 "$(status_of "$r")"
r=$(call POST /api/v1/identity/subscription/renew "$BRAND" '{}')
check "renewing a paid period too early is refused" 422 "$(status_of "$r")"

r=$(call PUT /api/v1/customers/loyalty/rules "$BRAND" '{"isActive":true,"amountPerPoint":1000,"redeemValuePerPoint":1000,"minPointsToRedeem":10,"silverThreshold":100,"goldThreshold":500}')
check "loyalty rules saved" 200 "$(status_of "$r")"
r=$(call POST /api/v1/identity/auth/register "" "{\"tenantCode\":\"smoke-$STAMP\",\"fullName\":\"Khach Hang\",\"email\":\"member-$STAMP@example.com\",\"phone\":\"0900000099\",\"password\":\"$BRAND_PASSWORD\",\"confirmPassword\":\"$BRAND_PASSWORD\"}")
check "member registers" 201 "$(status_of "$r")"
MEMBER_ID=$(body_of "$r" | json_field id)
MEMBER=$(call POST /api/v1/identity/auth/login "" "{\"login\":\"member-$STAMP@example.com\",\"password\":\"$BRAND_PASSWORD\"}" | sed "s/ [0-9]*$//" | json_field accessToken)
wait_until "the member profile exists in Customer service" "call GET /api/v1/customers/me \"\$MEMBER\" | sed 's/ [0-9]*\$//' | grep -c fullName" 1

r=$(call POST /api/v1/orders "$CASHIER" "{\"type\":\"Takeaway\",\"customerId\":\"$MEMBER_ID\",\"items\":[{\"productId\":\"$PRODUCT\",\"quantity\":2}]}")
check "order for the member" 201 "$(status_of "$r")"
ORDER3=$(body_of "$r" | json_field id)
r=$(call POST /api/v1/payments/transactions/cash "$CASHIER" "{\"orderId\":\"$ORDER3\",\"amount\":70000}")
check "member's order paid in cash" 201 "$(status_of "$r")"
wait_until "points are awarded after payment (70000 / 1000)" "call GET /api/v1/customers/me/loyalty \"\$MEMBER\" | sed 's/ [0-9]*\$//' | grep -o '\"pointsBalance\":[0-9]*'" '"pointsBalance":70'

r=$(call POST /api/v1/customers/me/redemptions "$MEMBER" '{"points":50}')
check "points turned into a voucher" 201 "$(status_of "$r")"
VOUCHER=$(body_of "$r" | json_field code)
case "$(body_of "$r")" in *'"pointsBalance":20'*) check "50 points were spent" yes yes ;; *) check "50 points were spent" yes no ;; esac

r=$(call POST /api/v1/orders "$CASHIER" "{\"type\":\"Takeaway\",\"customerId\":\"$MEMBER_ID\",\"promotionCode\":\"$VOUCHER\",\"items\":[{\"productId\":\"$PRODUCT\",\"quantity\":2}]}")
check "the voucher discounts the next order" 201 "$(status_of "$r")"
ORDER4=$(body_of "$r" | json_field id)
case "$(body_of "$r")" in *'"finalAmount":20000'*) check "70000 - 50000 voucher = 20000" yes yes ;; *) check "70000 - 50000 voucher = 20000" yes no ;; esac
wait_until "the voucher shows as used" "call GET /api/v1/customers/me/vouchers \"\$MEMBER\" | sed 's/ [0-9]*\$//' | grep -o '\"status\":\"[A-Za-z]*\"' | head -1" '"status":"Redeemed"'
r=$(call POST "/api/v1/orders/$ORDER4/cancel" "$CASHIER" '{"reason":"Customer left"}')
check "the unpaid order is cancelled" 200 "$(status_of "$r")"
wait_until "the voucher is given back" "call GET /api/v1/customers/me/vouchers \"\$MEMBER\" | sed 's/ [0-9]*\$//' | grep -o '\"status\":\"[A-Za-z]*\"' | head -1" '"status":"Active"'

# ------------------------------------------------------------------ pickup orders, self-service cancel and feedback (stage A)
echo
echo "== pickup orders"
first_open_slot() { # prints the start of the first open slot of a day (today, else tomorrow)
  for day in "$(date +%F)" "$(date -d tomorrow +%F 2>/dev/null || date -v+1d +%F)"; do
    slot=$(call GET "/api/v1/orders/pickup-slots/available?branchId=$BRANCH&date=$day" "$MEMBER" | sed 's/ [0-9]*$//' \
      | grep -o '"startUtc":"[^"]*","endUtc":"[^"]*","capacity":[0-9]*,"booked":[0-9]*,"remaining":[0-9]*,"locked":false,"available":true' | head -1 | sed 's/"startUtc":"\([^"]*\)".*/\1/')
    [ -n "$slot" ] && { echo "$slot"; return; }
  done
}
SLOT=$(first_open_slot)
[ -n "$SLOT" ] && check "the member sees an open pickup slot" yes yes || check "the member sees an open pickup slot" yes no

pickup() { # idempotency-key -> prints "<status> <body>" for the order of the member at the first open slot
  curl -s -w ' %{http_code}' -X POST "$GATEWAY/api/v1/orders/pickup" -H "Content-Type: application/json" -H "Authorization: Bearer $MEMBER" -H "Idempotency-Key: $1" \
    -d "{\"branchId\":\"$BRANCH\",\"slotStartUtc\":\"$SLOT\",\"items\":[{\"productId\":\"$PRODUCT\",\"quantity\":1,\"note\":\"less ice\"}]}"
}
r=$(pickup "smoke-$STAMP-a")
check "the member orders takeaway for a slot" 201 "$(status_of "$r")"
PICKUP_A=$(body_of "$r" | json_field id)
case "$(body_of "$r")" in *'"finalAmount":35000'*) check "the price comes from the menu" yes yes ;; *) check "the price comes from the menu" yes no ;; esac
r=$(pickup "smoke-$STAMP-a")
check "the same Idempotency-Key gives back the same order" 200 "$(status_of "$r")"
[ "$(body_of "$r" | json_field id)" = "$PICKUP_A" ] && check "same order id" yes yes || check "same order id" yes no

r=$(call POST /api/v1/payments/transactions "$MEMBER" "{\"orderId\":\"$PICKUP_A\",\"branchId\":\"$BRANCH\",\"amount\":35000,\"providerCode\":\"Mock\"}")
check "the member starts the online payment" 201 "$(status_of "$r")"
PAY_A=$(body_of "$r" | json_field id)
r=$(call POST "/api/v1/payments/transactions/$PAY_A/simulate" "$MEMBER" '{"outcome":"success"}')
if [ "$(status_of "$r")" = "403" ]; then
  echo "SKIP  provider simulation is disabled here (set PAYMENTS_ALLOW_SIMULATION=true in .env for a demo)"
else
  check "the provider confirms the payment" 200 "$(status_of "$r")"
  wait_until "the pickup order becomes Confirmed" "call GET /api/v1/orders/$PICKUP_A \"\$MEMBER\" | sed 's/ [0-9]*\$//' | field_of status" Confirmed
  wait_until "the bar queue shows the pickup order" "call GET /api/v1/kitchen/queue \"\$BARISTA\" | grep -c $PICKUP_A" 1
  TICKET_A=$(call GET /api/v1/kitchen/queue "$BARISTA" | sed 's/ [0-9]*$//' | grep -o "\"id\":\"[^\"]*\"[^{}]*\"orderId\":\"$PICKUP_A\"" | head -1 | json_field id)
  [ -z "$TICKET_A" ] && TICKET_A=$(call GET /api/v1/kitchen/queue "$BARISTA" | sed 's/ [0-9]*$//' | json_field id)
  call PUT "/api/v1/kitchen/tickets/$TICKET_A/status" "$BARISTA" '{"status":"Brewing"}' > /dev/null
  call PUT "/api/v1/kitchen/tickets/$TICKET_A/status" "$BARISTA" '{"status":"ReadyForPickup"}' > /dev/null
  wait_until "the pickup order becomes Ready" "call GET /api/v1/orders/$PICKUP_A \"\$CASHIER\" | sed 's/ [0-9]*\$//' | field_of status" Ready
  r=$(call POST "/api/v1/orders/$PICKUP_A/cancel" "$MEMBER" '{"reason":"too late"}')
  check "the member cannot cancel once the bar has started" 422 "$(status_of "$r")"
  r=$(call POST "/api/v1/orders/$PICKUP_A/deliver" "$CASHIER" '{}')
  check "the cashier hands the pickup order over" 200 "$(status_of "$r")"
  wait_until "the pickup order is Completed" "call GET /api/v1/orders/$PICKUP_A \"\$CASHIER\" | sed 's/ [0-9]*\$//' | field_of status" Completed
  r=$(call POST "/api/v1/orders/$PICKUP_A/feedback" "$MEMBER" '{"rating":5,"comment":"Ngon"}')
  check "the member rates the order" 201 "$(status_of "$r")"
  r=$(call POST "/api/v1/orders/$PICKUP_A/feedback" "$MEMBER" '{"rating":1}')
  check "a second rating is refused" 409 "$(status_of "$r")"
  r=$(call GET "/api/v1/orders/feedbacks" "$MANAGER")
  check "the manager reads the ratings" 200 "$(status_of "$r")"
  case "$(body_of "$r")" in *Ngon*) check "the rating is listed" yes yes ;; *) check "the rating is listed" yes no ;; esac
  r=$(call GET "/api/v1/orders/feedbacks/mine" "$MEMBER")
  check "the member lists their own ratings" 200 "$(status_of "$r")"
  case "$(body_of "$r")" in *Ngon*) check "the rating is in my list" yes yes ;; *) check "the rating is in my list" yes no ;; esac
  r=$(call GET "/api/v1/orders/$PICKUP_A" "$MEMBER")
  case "$(body_of "$r")" in *'"hasFeedback":true'*) check "the order says it was rated" yes yes ;; *) check "the order says it was rated" yes no ;; esac
  r=$(call GET "/api/v1/orders/feedbacks/mine" "$CASHIER")
  check "staff have no list of my ratings" 403 "$(status_of "$r")"
fi

r=$(pickup "smoke-$STAMP-b")
check "a second pickup order" 201 "$(status_of "$r")"
PICKUP_B=$(body_of "$r" | json_field id)
r=$(call POST "/api/v1/orders/$PICKUP_B/cancel" "$MEMBER" '{"reason":"changed my mind"}')
check "the member cancels an unpaid order" 200 "$(status_of "$r")"
r=$(call POST "/api/v1/orders/$PICKUP_B/cancel" "$CASHIER" '{"reason":"again"}')
check "cancelling twice is refused" 409 "$(status_of "$r")"

r=$(pickup "smoke-$STAMP-c")
PICKUP_C=$(body_of "$r" | json_field id)
r=$(call POST /api/v1/payments/transactions "$MEMBER" "{\"orderId\":\"$PICKUP_C\",\"branchId\":\"$BRANCH\",\"amount\":35000,\"providerCode\":\"Mock\"}")
PAY_C=$(body_of "$r" | json_field id)
r=$(call POST "/api/v1/payments/transactions/$PAY_C/simulate" "$MEMBER" '{"outcome":"success"}')
if [ "$(status_of "$r")" != "403" ]; then
  wait_until "the third pickup order is paid" "call GET /api/v1/orders/$PICKUP_C/payment \"\$MEMBER\" | sed 's/ [0-9]*\$//' | field_of paymentStatus" Paid
  r=$(call POST "/api/v1/orders/$PICKUP_C/cancel" "$MEMBER" '{"reason":"changed my mind"}')
  check "the member cancels a paid order before the slot is locked" 200 "$(status_of "$r")"
  wait_until "the refund by the brand policy is paid back" "call GET /api/v1/orders/$PICKUP_C \"\$MANAGER\" | sed 's/ [0-9]*\$//' | field_of refundStatus" Refunded
fi


# ------------------------------------------------------------------ size and topping options (surcharges)
echo
echo "== item options"
r=$(call POST /api/v1/menu/products "$BRAND" "{\"categoryId\":\"$CATEGORY\",\"name\":\"Tra sua\",\"basePrice\":40000,\"optionGroups\":[{\"name\":\"Size\",\"kind\":\"Size\",\"selectionType\":\"Single\",\"isRequired\":true,\"options\":[{\"name\":\"M\",\"priceDelta\":0,\"isDefault\":true},{\"name\":\"L\",\"priceDelta\":6000,\"isDefault\":false}]},{\"name\":\"Nhiet do\",\"kind\":\"Other\",\"selectionType\":\"Single\",\"isRequired\":false,\"options\":[{\"name\":\"Nong\",\"priceDelta\":0,\"isDefault\":false},{\"name\":\"Lanh\",\"priceDelta\":0,\"isDefault\":false}]},{\"name\":\"Topping\",\"kind\":\"Topping\",\"selectionType\":\"Multiple\",\"isRequired\":false,\"maxSelections\":2,\"options\":[{\"name\":\"Tran chau\",\"priceDelta\":7000,\"isDefault\":false,\"requiresGroup\":\"Nhiet do\",\"requiresOption\":\"Lanh\"},{\"name\":\"Thach\",\"priceDelta\":5000,\"isDefault\":false}]}]}")
check "a product with size and topping options is created" 201 "$(status_of "$r")"
TEA=$(body_of "$r" | json_field id)
OPT_L=$(body_of "$r" | grep -o '"id":"[^"]*","name":"L"' | head -1 | json_field id)
OPT_COLD=$(body_of "$r" | grep -o '"id":"[^"]*","name":"Lanh"' | head -1 | json_field id)
OPT_PEARL=$(body_of "$r" | grep -o '"id":"[^"]*","name":"Tran chau"' | head -1 | json_field id)
OPT_JELLY=$(body_of "$r" | grep -o '"id":"[^"]*","name":"Thach"' | head -1 | json_field id)
r=$(call PUT "/api/v1/menu/products/$TEA/branches/$BRANCH" "$BRAND" '{"isPublished":true}')
check "the product is published to the branch" 200 "$(status_of "$r")"

r=$(call POST /api/v1/orders "$CASHIER" "{\"type\":\"Takeaway\",\"items\":[{\"productId\":\"$TEA\",\"quantity\":2,\"optionIds\":[\"$OPT_L\",\"$OPT_COLD\",\"$OPT_PEARL\"]}]}")
check "an order with size L and a topping" 201 "$(status_of "$r")"
case "$(body_of "$r")" in *'"finalAmount":106000'*) check "2 x (40000 + 6000 + 7000) = 106000" yes yes ;; *) check "2 x (40000 + 6000 + 7000) = 106000" yes no ;; esac
r=$(call POST /api/v1/orders "$CASHIER" "{\"type\":\"Takeaway\",\"items\":[{\"productId\":\"$TEA\",\"quantity\":1,\"optionIds\":[\"$OPT_PEARL\"]}]}")
check "a topping that needs a cold drink is refused without it" 422 "$(status_of "$r")"
r=$(call POST /api/v1/orders "$CASHIER" "{\"type\":\"Takeaway\",\"items\":[{\"productId\":\"$TEA\",\"quantity\":1}]}")
case "$(body_of "$r")" in *'"finalAmount":40000'*) check "the default size is used when none is sent" yes yes ;; *) check "the default size is used when none is sent" yes no ;; esac
r=$(call POST /api/v1/orders "$CASHIER" "{\"type\":\"Takeaway\",\"items\":[{\"productId\":\"$TEA\",\"quantity\":1,\"optionIds\":[\"$OPT_JELLY\",\"$OPT_PEARL\",\"$OPT_COLD\",\"$OPT_L\"]}]}")
check "two toppings are within the limit" 201 "$(status_of "$r")"


# ------------------------------------------------------------------ table QR ordering without an account (stage B)
echo
echo "== table QR ordering"
r=$(call POST /api/v1/tables/areas "$BRAND" "{\"branchId\":\"$BRANCH\",\"name\":\"Tang tret\"}")
check "an area is created" 201 "$(status_of "$r")"
AREA=$(body_of "$r" | json_field id)
r=$(call POST /api/v1/tables/dining-tables "$BRAND" "{\"branchId\":\"$BRANCH\",\"areaId\":\"$AREA\",\"tableCode\":\"A1\",\"capacity\":4}")
check "a table is created with a QR code" 201 "$(status_of "$r")"
QRTOKEN=$(body_of "$r" | json_field qrToken)

r=$(call POST /api/v1/orders/qr/scan "" "{\"qrToken\":\"$QRTOKEN\"}")
check "a guest scans the table code" 200 "$(status_of "$r")"
GUEST=$(body_of "$r" | json_field sessionToken)
SESSION=$(body_of "$r" | json_field sessionId)
r=$(call POST /api/v1/orders/qr/scan "" "{\"qrToken\":\"$QRTOKEN\"}")
[ "$(body_of "$r" | json_field sessionId)" = "$SESSION" ] && check "a second phone joins the same session" yes yes || check "a second phone joins the same session" yes no
r=$(call POST /api/v1/orders/qr/scan "" '{"qrToken":"not-a-table"}')
check "an unknown code is refused" 404 "$(status_of "$r")"

qr() { # method path body -> "<status> <body>" with the table session header
  curl -s -w ' %{http_code}' -X "$1" "$GATEWAY$2" -H "Content-Type: application/json" -H "X-Table-Session: $GUEST" ${3:+-d "$3"}
}
r=$(curl -s -w ' %{http_code}' "$GATEWAY/api/v1/orders/qr/cart")
check "the cart needs the session token" 401 "$(status_of "$r")"
r=$(qr POST /api/v1/orders/qr/cart/items "{\"productId\":\"$PRODUCT\",\"quantity\":2,\"note\":\"less ice\"}")
check "an item goes into the shared cart" 200 "$(status_of "$r")"
case "$(body_of "$r")" in *'"total":70000'*) check "the cart is priced from the menu (2 x 35000)" yes yes ;; *) check "the cart is priced from the menu (2 x 35000)" yes no ;; esac
r=$(qr POST /api/v1/orders/qr/cart/items "{\"productId\":\"$TEA\",\"quantity\":1,\"optionIds\":[\"$OPT_L\"]}")
check "an item with a size is added" 200 "$(status_of "$r")"
case "$(body_of "$r")" in *'"total":116000'*) check "70000 + (40000 + 6000) = 116000" yes yes ;; *) check "70000 + (40000 + 6000) = 116000" yes no ;; esac
r=$(qr GET /api/v1/orders/qr/cart)
check "the other phone reads the same cart" 200 "$(status_of "$r")"

r=$(qr POST /api/v1/orders/qr/checkout '{"note":"Table A1"}')
check "the table orders (round 1)" 201 "$(status_of "$r")"
QORDER=$(body_of "$r" | json_field id)
case "$(body_of "$r")" in *'"tableNumber":"A1"'*) check "the order carries the table" yes yes ;; *) check "the order carries the table" yes no ;; esac
r=$(qr POST /api/v1/orders/qr/checkout '{}')
check "an empty cart is not an order" 422 "$(status_of "$r")"
wait_until "the bar queue shows the table order at once" "call GET /api/v1/kitchen/queue \"\$BARISTA\" | grep -c $QORDER" 1

r=$(qr POST /api/v1/orders/qr/cart/items "{\"productId\":\"$PRODUCT\",\"quantity\":1}")
r=$(qr POST /api/v1/orders/qr/checkout '{}')
check "the table orders again (round 2)" 201 "$(status_of "$r")"
QORDER2=$(body_of "$r" | json_field id)
r=$(qr GET /api/v1/orders/qr/session/bill)
check "the table bill" 200 "$(status_of "$r")"
case "$(body_of "$r")" in *'"total":151000'*'"due":151000'*) check "bill = 116000 + 35000, all due" yes yes ;; *) check "bill = 116000 + 35000, all due" yes no ;; esac
r=$(call GET /api/v1/orders/qr/sessions "$CASHIER")
check "the cashier lists the open tables" 200 "$(status_of "$r")"
case "$(body_of "$r")" in *'"tableCode":"A1"'*) check "table A1 is listed" yes yes ;; *) check "table A1 is listed" yes no ;; esac

r=$(call POST /api/v1/payments/transactions/cash "$CASHIER" "{\"orderId\":\"$QORDER\",\"amount\":116000}")
check "round 1 is paid at the counter" 201 "$(status_of "$r")"
wait_until "the session stays open while a round is unpaid" "call GET /api/v1/orders/qr/sessions \"\$CASHIER\" | sed 's/ [0-9]*\$//' | grep -c A1" 1
r=$(call POST /api/v1/payments/transactions/cash "$CASHIER" "{\"orderId\":\"$QORDER2\",\"amount\":35000}")
check "round 2 is paid at the counter" 201 "$(status_of "$r")"
wait_until "the session closes when everything is paid" "call GET /api/v1/orders/qr/sessions \"\$CASHIER\" | sed 's/ [0-9]*\$//' | grep -c A1" 0
r=$(qr POST /api/v1/orders/qr/cart/items "{\"productId\":\"$PRODUCT\",\"quantity\":1}")
check "a closed session takes no more orders" 409 "$(status_of "$r")"
r=$(qr GET /api/v1/orders/qr/session/bill)
check "the closed session bill can still be read" 200 "$(status_of "$r")"
r=$(call POST /api/v1/orders/qr/scan "" "{\"qrToken\":\"$QRTOKEN\"}")
[ "$(body_of "$r" | json_field sessionId)" != "$SESSION" ] && check "a new scan opens a new session" yes yes || check "a new scan opens a new session" yes no


# ------------------------------------------------------------------ remake, unfulfillable and no-show
echo
echo "== remake, unfulfillable, no-show"
new_paid_order() { # prints the id of a new counter order paid in cash
  o=$(call POST /api/v1/orders "$CASHIER" "{\"type\":\"Takeaway\",\"items\":[{\"productId\":\"$PRODUCT\",\"quantity\":1}]}" | sed 's/ [0-9]*$//' | json_field id)
  call POST /api/v1/payments/transactions/cash "$CASHIER" "{\"orderId\":\"$o\",\"amount\":35000}" > /dev/null
  echo "$o"
}
ticket_of() { call GET /api/v1/kitchen/queue "$BARISTA" | sed 's/ [0-9]*$//' | grep -o "\"id\":\"[^\"]*\",\"orderId\":\"$1\"" | head -1 | json_field id; }

RMK=$(new_paid_order)
wait_until "the order to remake reaches the bar" "call GET /api/v1/kitchen/queue \"\$BARISTA\" | grep -c $RMK" 1
RMK_T=$(ticket_of "$RMK")
call PUT "/api/v1/kitchen/tickets/$RMK_T/status" "$BARISTA" '{"status":"Brewing"}' > /dev/null
call PUT "/api/v1/kitchen/tickets/$RMK_T/status" "$BARISTA" '{"status":"ReadyForPickup"}' > /dev/null
wait_until "the order is Ready" "call GET /api/v1/orders/$RMK \"\$CASHIER\" | sed 's/ [0-9]*\$//' | field_of status" Ready
r=$(call POST "/api/v1/kitchen/tickets/$RMK_T/remake" "$CASHIER" '{"reason":"spilled"}')
check "a cashier cannot send an order back to the bar" 403 "$(status_of "$r")"
r=$(call POST "/api/v1/kitchen/tickets/$RMK_T/remake" "$BARISTA" '{"reason":"spilled"}')
check "the bar makes the ready order again" 200 "$(status_of "$r")"
wait_until "the order is Preparing again" "call GET /api/v1/orders/$RMK \"\$CASHIER\" | sed 's/ [0-9]*\$//' | field_of status" Preparing
r=$(call POST "/api/v1/orders/$RMK/deliver" "$CASHIER" '{}')
check "the cashier cannot hand over an order that is being remade" 422 "$(status_of "$r")"
call PUT "/api/v1/kitchen/tickets/$RMK_T/status" "$BARISTA" '{"status":"ReadyForPickup"}' > /dev/null
wait_until "the remade order is Ready again" "call GET /api/v1/orders/$RMK \"\$CASHIER\" | sed 's/ [0-9]*\$//' | field_of status" Ready
r=$(call POST "/api/v1/orders/$RMK/deliver" "$CASHIER" '{}')
check "the remade order is handed over" 200 "$(status_of "$r")"

UNF=$(new_paid_order)
wait_until "the order to give up reaches the bar" "call GET /api/v1/kitchen/queue \"\$BARISTA\" | grep -c $UNF" 1
r=$(call POST "/api/v1/orders/$UNF/unfulfillable" "$BARISTA" '{"reason":"out of milk"}')
check "the bar gives up an order it cannot make" 200 "$(status_of "$r")"
wait_until "the order is Unfulfillable" "call GET /api/v1/orders/$UNF \"\$CASHIER\" | sed 's/ [0-9]*\$//' | field_of status" Unfulfillable
wait_until "the customer is refunded in full" "call GET /api/v1/orders/$UNF \"\$MANAGER\" | sed 's/ [0-9]*\$//' | field_of refundStatus" Refunded
wait_until "the ticket leaves the bar queue" "call GET /api/v1/kitchen/queue \"\$BARISTA\" | grep -c $UNF" 0
r=$(call POST "/api/v1/orders/$UNF/unfulfillable" "$BARISTA" '{"reason":"again"}')
check "an order is given up only once" 409 "$(status_of "$r")"

NSH=$(new_paid_order)
r=$(call POST "/api/v1/orders/$NSH/no-show" "$CASHIER" '{"reason":"nobody came"}')
check "a no-show is refused before the waiting time has passed" 422 "$(status_of "$r")"
r=$(call POST "/api/v1/orders/$NSH/no-show" "$BARISTA" '{"reason":"nobody came"}')
check "the bar cannot record a no-show" 403 "$(status_of "$r")"


# ------------------------------------------------------------------ live order tracking and member notifications
echo
echo "== live tracking and notifications"
r=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$GATEWAY/hubs/orders/negotiate?negotiateVersion=1&sessionToken=$GUEST")
check "the order hub negotiates through the gateway for a table guest" 200 "$r"
r=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$GATEWAY/hubs/orders/negotiate?negotiateVersion=1&access_token=$MEMBER")
check "the order hub negotiates through the gateway for a member" 200 "$r"

wait_until "the member has notifications about the orders they paid" "call GET /api/v1/customers/me/notifications \"\$MEMBER\" | sed 's/ [0-9]*\$//' | grep -c '\"kind\":\"Paid\"'" 1
r=$(call GET /api/v1/customers/me/notifications/unread-count "$MEMBER")
check "the unread count" 200 "$(status_of "$r")"
case "$(body_of "$r")" in *'"unread":0'*) check "the member has unread notifications" yes no ;; *) check "the member has unread notifications" yes yes ;; esac
r=$(call POST /api/v1/customers/me/notifications/read-all "$MEMBER" '{}')
check "the member marks everything as read" 200 "$(status_of "$r")"
r=$(call GET /api/v1/customers/me/notifications/unread-count "$MEMBER")
case "$(body_of "$r")" in *'"unread":0'*) check "nothing is unread any more" yes yes ;; *) check "nothing is unread any more" yes no ;; esac
r=$(call GET /api/v1/customers/me/notifications "$CASHIER")
check "staff have no notifications" 403 "$(status_of "$r")"

r=$(call GET "/api/v1/identity/public/subscription-plans" "")
check "the landing page reads the plans without logging in" 200 "$(status_of "$r")"
r=$(call GET "/api/v1/identity/subscription-plans" "")
check "the plan list at the admin path is open to visitors (active plans only)" 200 "$(status_of "$r")"
r=$(call POST "/api/v1/identity/subscription-plans" "" '{"name":"Hack","tier":"Basic","fee":1,"billingPeriod":"Monthly"}')
check "creating a plan still needs the Super Admin" 401 "$(status_of "$r")"

# ------------------------------------------------------------------ self-service brand registration, public page, contact form
echo
echo "== brand registration and public pages"
# register, login and contact share the 10-per-minute budget of the gateway: start this block in a fresh minute
echo "(waiting one minute for the login rate window)"; sleep 62
REG_CODE="reg-$STAMP"
REG_EMAIL="reg-owner-$STAMP@example.com"
r=$(call POST /api/v1/identity/auth/register-brand "" "{\"brandName\":\"Reg $STAMP\",\"code\":\"$REG_CODE\",\"ownerFullName\":\"Reg Owner\",\"email\":\"$REG_EMAIL\",\"phone\":\"0900000077\",\"password\":\"$BRAND_PASSWORD\",\"confirmPassword\":\"$BRAND_PASSWORD\",\"description\":\"Smoke brand\"}")
check "an owner registers a brand" 201 "$(status_of "$r")"
REG_ID=$(body_of "$r" | json_field tenantId)
case "$(body_of "$r")" in *'"status":"Pending"'*) check "the new brand is Pending" yes yes ;; *) check "the new brand is Pending" yes no ;; esac
r=$(call POST /api/v1/identity/auth/register-brand "" "{\"brandName\":\"Reg $STAMP\",\"code\":\"$REG_CODE\",\"ownerFullName\":\"Reg Owner\",\"email\":\"other-$REG_EMAIL\",\"password\":\"$BRAND_PASSWORD\",\"confirmPassword\":\"$BRAND_PASSWORD\"}")
check "the same brand code is refused" 409 "$(status_of "$r")"
r=$(call POST /api/v1/identity/auth/login "" "{\"login\":\"$REG_EMAIL\",\"password\":\"$BRAND_PASSWORD\"}")
check "the owner cannot sign in before approval" 403 "$(status_of "$r")"
case "$(body_of "$r")" in *tenant.pending_approval*) check "the answer says the brand awaits approval" yes yes ;; *) check "the answer says the brand awaits approval" yes no ;; esac
r=$(call GET "/api/v1/identity/public/brands/$REG_CODE" "")
check "a pending brand has no public page" 404 "$(status_of "$r")"
r=$(call GET "/api/v1/identity/tenants?status=Pending" "$SUPER")
case "$(body_of "$r")" in *"$REG_CODE"*) check "the Super Admin sees the pending registration" yes yes ;; *) check "the Super Admin sees the pending registration" yes no ;; esac
r=$(call POST "/api/v1/identity/tenants/$REG_ID/approve" "$BRAND" '{}')
check "a brand admin cannot approve" 403 "$(status_of "$r")"
r=$(call POST "/api/v1/identity/tenants/$REG_ID/approve" "$SUPER" '{}')
check "the Super Admin approves the registration" 200 "$(status_of "$r")"
r=$(call POST "/api/v1/identity/tenants/$REG_ID/approve" "$SUPER" '{}')
check "a registration is decided once" 409 "$(status_of "$r")"
r=$(call POST /api/v1/identity/auth/login "" "{\"login\":\"$REG_EMAIL\",\"password\":\"$BRAND_PASSWORD\"}")
check "the owner signs in after approval" 200 "$(status_of "$r")"
REG_TOKEN=$(body_of "$r" | json_field accessToken)
r=$(call PUT /api/v1/identity/brand/profile "$REG_TOKEN" '{"description":"Ca phe rang xay","logoUrl":"https://cdn.example.com/logo.png","primaryColor":"#6F4E37","secondaryColor":"#FFF8E7"}')
check "the brand admin edits the public profile" 200 "$(status_of "$r")"
r=$(call PUT /api/v1/identity/brand/profile "$REG_TOKEN" '{"logoUrl":"javascript:alert(1)"}')
check "a script link is refused as a logo" 400 "$(status_of "$r")"
r=$(call GET "/api/v1/identity/public/brands/$REG_CODE" "")
check "the public brand page opens without signing in" 200 "$(status_of "$r")"
case "$(body_of "$r")" in *'"logoUrl":"https://cdn.example.com/logo.png"'*'#6F4E37'*|*'#6F4E37'*'"logoUrl":"https://cdn.example.com/logo.png"'*) check "the page carries logo and colors" yes yes ;; *) check "the page carries logo and colors" yes no ;; esac
r=$(call GET "/api/v1/identity/public/brands/smoke-$STAMP" "")
check "an active brand has a public page too" 200 "$(status_of "$r")"
r=$(call GET "/api/v1/identity/branches/active" "$BRAND")
check "a brand admin lists the active branches of their brand without naming it" 200 "$(status_of "$r")"
case "$(body_of "$r")" in *"$BRANCH"*) check "the own branch is in the list" yes yes ;; *) check "the own branch is in the list" yes no ;; esac
r=$(call GET "/api/v1/identity/branches/active" "")
check "a visitor still has to name the brand" 400 "$(status_of "$r")"

r=$(call POST /api/v1/identity/public/contact "" "{\"fullName\":\"Khach Lien He\",\"email\":\"contact-$STAMP@example.com\",\"subject\":\"Hoi gia\",\"message\":\"Cho minh xin bao gia\"}")
check "a visitor sends the contact form" 202 "$(status_of "$r")"
r=$(call GET "/api/v1/identity/contact-messages?status=New" "$SUPER")
check "the Super Admin reads the messages" 200 "$(status_of "$r")"
case "$(body_of "$r")" in *"contact-$STAMP@example.com"*) check "the message is in the database" yes yes ;; *) check "the message is in the database" yes no ;; esac
MSG_ID=$(body_of "$r" | grep -o "\"id\":\"[^\"]*\",\"fullName\":\"Khach Lien He\"" | head -1 | json_field id)
r=$(call POST "/api/v1/identity/contact-messages/$MSG_ID/handled" "$SUPER" '{}')
check "the message is marked handled" 200 "$(status_of "$r")"
r=$(call GET "/api/v1/identity/contact-messages" "$BRAND")
check "a brand admin cannot read the contact messages" 403 "$(status_of "$r")"

r=$(call GET "/api/v1/reporting/branch?branchId=$BRANCH" "$BRAND")
check "branch report" 200 "$(status_of "$r")"

r=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$GATEWAY/hubs/kitchen/negotiate?negotiateVersion=1&access_token=$BARISTA")
check "SignalR hub negotiates through the gateway with the token" 200 "$r"
r=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$GATEWAY/hubs/kitchen/negotiate?negotiateVersion=1")
check "SignalR hub refuses an anonymous client" 401 "$r"
echo
if [ "$failed" -eq 0 ]; then echo "ALL CHECKS PASSED"; else echo "$failed CHECK(S) FAILED"; exit 1; fi
