# Local floor-plan expansion: ten tables per area

Date: 2026-11-04 (Asia/Saigon).

User requested ten tables per area and different area names between branches.
Target remains Docker project `aicos-main`, tenant `aicos-cashier-local`, database
`aicos_table_reservation`, schema `table_reservation`. This is persisted local
PostgreSQL data, not a Mobile mock or a change to the legacy Windows databases.

| Branch | Areas | Tables |
| --- | --- | --- |
| Hồ Con Rùa (Q.3) | Tầng trệt, Lầu 1, Ngoài sân | 30 |
| Nguyễn Huệ (Q.1) | Tầng trệt, Lầu 1, Ban công | 30 |
| Phan Xích Long (Phú Nhuận) | Tầng trệt, Phòng máy lạnh, Sân vườn | 30 |

Nine areas now have exactly ten tables each: 90 tables in total.

## Applied changes through existing APIs

- Authenticated the existing local BrandAdmin using its current-user DPAPI credential.
  Verified account/tenant scope and all three floor plans before mutations.
- PUT `/api/v1/tables/areas/{id}` renamed three areas, preserving their IDs.
  Nguyen Hue: outside -> balcony. Phan Xich Long: upper floor -> air-conditioned
  room; outside -> garden. Ground floor is shared; the first branch was unchanged.
- POST `/api/v1/tables/dining-tables/batch` added 18 tables per branch, 54 total.
  Existing codes were checked before additions. Each original area ID received
  its own missing codes up to 10 using its original S/L/N prefix.
- Existing 36 table IDs, QR tokens, codes and records were checked and preserved.
  Newly added tables use capacities 2/4/4/6 in rotation, 2-column/5-row positions,
  and status Available. No additional areas were created.
- Read back each area through the API, then logged out the setup session.

No new endpoints, server code, migrations, schema/ACL changes, DELETE, reset,
credential changes or order/payment fixtures were used. The existing plan limit
of 50 tables per branch accommodates the resulting 30 tables per branch.
Passwords, tokens and table QR values are not recorded in Git or this document.

## Verification and runtime interruption

- API readback: three areas of ten tables in each branch; original IDs/QR retained.
- During final verification the Docker stack stopped. PostgreSQL exit=0 and
  OOM=false; the cause of the stop was not established. Existing containers for
  PostgreSQL/Redis/Identity/Table/Ordering/Gateway were resumed, with migration
  startup false verified before the relevant .NET services started.
- Independent SQL `BEGIN READ ONLY` after PostgreSQL resumed: areas=9, tables=90,
  bad_area_counts=0, orphan_tables=0. Data remained persisted; no reprovisioning.
- Mobile shared compact cards/grids were updated in the separate Mobile repo.
  Analyzer: 0 issues. Full Flutter suite with Demo Mode: 112 PASS, 0 FAIL;
  responsive coverage includes 320/360/393/430px and enlarged text. Flutter widget
  tests use fixtures/mock HTTP and are separate from the real API/SQL checks.
- No new Mobile build was launched on a physical device in this task.
- No commit/push performed. Git does not distribute local PostgreSQL volume data.
