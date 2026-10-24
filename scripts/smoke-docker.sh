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

r=$(call GET "/api/v1/identity/public/subscription-plans" "")
check "the landing page reads the plans without logging in" 200 "$(status_of "$r")"
r=$(call GET "/api/v1/identity/subscription-plans" "")
check "the plan admin list stays closed to anonymous callers" 401 "$(status_of "$r")"

r=$(call GET "/api/v1/reporting/branch?branchId=$BRANCH" "$BRAND")
check "branch report" 200 "$(status_of "$r")"

r=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$GATEWAY/hubs/kitchen/negotiate?negotiateVersion=1&access_token=$BARISTA")
check "SignalR hub negotiates through the gateway with the token" 200 "$r"
r=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$GATEWAY/hubs/kitchen/negotiate?negotiateVersion=1")
check "SignalR hub refuses an anonymous client" 401 "$r"
echo
if [ "$failed" -eq 0 ]; then echo "ALL CHECKS PASSED"; else echo "$failed CHECK(S) FAILED"; exit 1; fi
