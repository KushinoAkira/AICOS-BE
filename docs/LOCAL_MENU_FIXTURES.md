# Local staff menu: drinks, food and fixed-price combos

Date: 2026-11-04 (Asia/Saigon).

## Applied data

Target: Docker project `aicos-main`, tenant `aicos-cashier-local`, database
`aicos_menu`, schema `menu`. The user requested 20-30 real menu products with
food/combos. Created 28 products in 6 categories and published them to the three
existing local branches using the existing Menu service APIs.

| Category | Products |
| --- | --- |
| Cà phê | 6 |
| Trà & Trà sữa | 6 |
| Đồ uống khác | 4 |
| Đồ ăn | 5 |
| Bánh ngọt | 3 |
| Combo | 4 |

16 drinks, 8 foods/cakes, 4 combos. Price range: VND 12,000-69,000 before option
surcharges. Drinks use suitable size/sugar/ice groups; tea supports toppings;
food supports heating. Required single-choice groups have valid defaults.

Combos: Bac xiu M + hotdog (55,000), milk coffee M + egg banh mi (49,000), peach
tea M + croissant (59,000), latte M + tiramisu (69,000). Each is a standalone
sellable product with fixed bundle contents in its Vietnamese name/description.
There is no combo composition model in the current service. This implementation
does not split a combo into child order lines, calculate ingredient consumption,
or support replacing components. No schema was added to imply those behaviors.

Image URLs are null; Mobile uses product tags to distinguish food/combo icons.

## Reusable provisioning files

- `docs/fixtures/local-staff-menu.psd1`: prices/names/categories, no credentials.
- `scripts/Initialize-LocalStaffMenu.ps1`: guarded API provisioning; UTF-8 BOM for
  Windows PowerShell 5.1. No direct SQL writes or migrations.

From the Backend repository root, with the existing main runtime started:

```powershell
.\scripts\Initialize-LocalStaffMenu.ps1
.\scripts\Initialize-LocalStaffMenu.ps1 -Apply
```

The first command previews only and does not login/create fixtures. The second
requires the existing current-user DPAPI local BrandAdmin credential outside Git.
It checks Docker project, running dependencies, Menu migration=false and the
`postgres/aicos_menu/menu_svc` target. Auth scope must match the local BrandAdmin.
It creates missing categories/products, does not overwrite differing existing
price/category/description, and publishes only the fixture product IDs. Existing
availability/price overrides are not reset. Unexpected large catalogs stop before
assuming one-page pagination. Interrupted work can be inspected and rerun without
deleting data; a cross-service transaction or destructive rollback is not used.

## Actual results

- Initial publish: branches=3, products=28, changed=84.
- Repeat run after fixing PowerShell array enumeration: changed=0; no duplicates.
- SQL `BEGIN READ ONLY`: categories=6, products=28, branch_products=84,
  option_groups=58, product_options=156.
- Each of the three real Cashier accounts read all 28 products from its staff
  menu, including 16 drink/8 food/4 combo tags; availability, positive prices and
  required defaults checked. Login sessions were revoked after verification.
- Ordering capabilities currently return counterIdempotency=true and
  counterTableSessions=true. No real order was submitted to assert this result.
- Menu container was resumed after migration=false verification; stopped core
  containers were also resumed for API access. No recreation/reset/volume deletion.
- No Backend application source, endpoint, EF migration, schema/ACL or credentials
  changed. No order/payment fixture or commit/push.

## Related Mobile change

Mobile now lets staff type a positive guest count or use +/- beyond seat capacity;
the seat count is informational. Demo submit no longer caps guests by seats.
API keeps the existing order-note convention for guest count; TableSession has
no dedicated guest count field added here. Future reporting/session-edit support
needs a reviewed structured contract/schema; this task does not claim it exists.

Flutter formatter/analyzer PASS (0 issues); full suite with Demo Mode: 118 PASS,
0 FAIL. Tests cover 12 guests for a two-seat table, validation/cancel, dynamic
food/combo categories, fixed bundle price and product/option-ID order requests.
Flutter tests use mocks; the SQL/Cashier GET verification above is real. No new
device build or Kitchen/Payment end-to-end order test was performed.

## Follow-up: expanded options

See [MENU_OPTIONS_EXPANSION.md](MENU_OPTIONS_EXPANSION.md): 69 groups / 290 options; 17 drink/combo topping groups per branch, no fixed two-choice cap. The initial counts above describe the original fixture run.
