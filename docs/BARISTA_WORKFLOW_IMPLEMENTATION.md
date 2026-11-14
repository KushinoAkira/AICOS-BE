# Barista acceptance, preparation and printing

Checkpoint: 2026-11-14 (Asia/Saigon). Source implementation; operational feature remains OFF. This document supersedes earlier automatic-label proposals. Payment, Bill and Serving code are unchanged.

## Workflow and ownership

1. Ordering persists the order; existing durable Kitchen Admission/Recovery creates one ticket and its sticker **metadata**. Metadata is not a physical print or a print job.
2. Barista explicitly accepts the current ticket/remake cycle. Existing tenant/order transaction lock plus request-key lock serializes competing actors. The transaction persists acceptance and exactly one SLIP job, then commits. No LABELS job is created.
3. A Kitchen background worker claims QUEUED jobs, commits its lease/fence, calls the configured printer agent outside the PostgreSQL transaction, then records the verified result under the same order lock.
4. Labels require explicit selection of ticket item IDs and copy counts in Mobile. Cancel and every GET are read-only. Reusing the same key/body returns the original job. A deliberate reprint is a separate job with requester/time/parent audit.
5. WholeOrder tickets complete preparation as READY only. CupV1 tickets reuse existing Start/Ready operations and timestamps per stable CupId. They never fall back to whole-order completion. Barista cannot claim for delivery, serve, collect payment or close a session.

Recovery holds block preparation and printing. Worker rejects queued jobs whose ticket changed table, cycle or cancellation/hold before dispatch; transfer during dispatch produces UNKNOWN. Reprints use the current table and cycle. No previous CupId, claim or sticker identity is reset.

## Additive API contracts

Prefix: /api/v1/kitchen/barista. Existing Gateway Kitchen catch-all routes are reused. Tenant/branch access is checked in Kitchen; Barista and existing management BarRoles are required. Feature OFF returns 503; forbidden roles return 403, missing authentication 401. Legacy API request/response contracts are retained.

| Method and path | Request / response |
| --- | --- |
| GET /tickets | branchId, status All/Waiting/Brewing/ReadyForPickup, history, tableNumber, offset, limit; items/offset/hasMore. Default 30, maximum 100. |
| GET /tickets/{ticketId} | Ticket, current-cycle acceptance, current area, authoritative Ordering orderedAtUtc (nullable), sentToBarAtUtc, item IDs/options, CupV1 metadata, latest 50 print jobs and server time. |
| POST /tickets/{ticketId}/accept | Idempotency-Key (8-80 chars), expectedStatus, expectedRemakeCount. Returns original automatic SLIP job on valid replay. Other actor/stale state/key-body conflict returns 409. |
| POST /tickets/{ticketId}/labels | Idempotency-Key, items [{itemId,copies}], optional reprintOf/confirmUnknownRisk. 1-50 distinct items, 1-99 copies each, maximum 200 labels. |
| POST /tickets/{ticketId}/reprint-slip | Idempotency-Key, reprintOf, confirmUnknownRisk when original UNKNOWN. Pending jobs cannot be reprinted; other-cycle jobs are rejected. |
| GET /print-jobs/{id} | Kind/status, requester, parent, created/started/verified printed timestamps, error and test-output marker. |
| GET /print-jobs/{id}/preview | Typed document + escaped HTML; no dispatch or printer status mutation. |
| GET /summary | Vietnam-day waiting/brewing/completed orders/cups and mean accepted-to-completed seconds. |
| GET /table-summary | Branch-wide active ticket/count aggregates, independent of pagination. |

Legacy ticket status updates and CupV1 Start/Ready require acceptance when the new feature is ON. Legacy Completed mutation is refused in this mode: serving remains a counter/staff workflow. With feature OFF, existing behavior is unchanged. No new Web client was available to test; backend contract/guard tests and default-OFF regression cover server compatibility only.

## Time and names

UTC is stored/returned; Mobile and print templates render UTC+7. acceptedAt/acceptedBy and completedAt/completedBy are server-authored, preserved on replay. Accepted name is snapshotted from trusted JWT; Mobile resolves completion names through the existing Identity display-name API and uses an explicit unknown label if unavailable.

Kitchen ticket createdAt is **admission time**, not the original order time. Detail and print creation obtain order createdAt through the existing authenticated Ordering GET, verify order/branch identity and use null/unknown if it is unavailable. They never invent an order time. Sent-to-Bar time currently means durable Kitchen admission, not a separately persisted dispatch timestamp. List cards explicitly label this time "Xu?ng Bar". Current area is obtained through Table API; neither service's tables are read directly.

## Print lifecycle and device contract

QUEUED -> PRINTING -> PRINTED / FAILED / UNKNOWN. No automatic retry of FAILED or UNKNOWN. An expired PRINTING lease becomes UNKNOWN, without another device POST. A printer error does not undo acceptance/preparation. Operator must inspect unknown output before explicitly requesting a new print. This deliberately trades automatic retry for avoiding duplicate physical output.

VerifiedPrintAgent sends JobId as Idempotency-Key plus HMAC-SHA256 signature of JobId|PayloadHash|REQUEST. The separate agent must durably deduplicate this key, verify the payload hash, send to the device and return matching JobId/status with signed JobId|PayloadHash|PRINTED **only after reliable device acknowledgement**. HTTP 200 alone is insufficient. Invalid ACK, timeout or ambiguous response is UNKNOWN. There is no automatic POST retry handler. Agent URL/key come from runtime configuration; keys never belong in source/logs. Use a trusted private network and TLS when crossing a trust boundary.

The test fixture returns signed TEST_PRINTED only. The adapter accepts it only when AllowTestPrinter=true, environment Development and exact scratch Kitchen database aicos_billstack_kitchen. Result carries testOutput=true and Mobile displays a MOCK warning. This is not proof of real hardware delivery. Fixture agent uses an in-memory dedup cache and must never be deployed as a production printer agent.

SLIP: AICOS Coffee, order, current location, real order time/unknown, acceptance time/name, quantities/customizations/notes and server request-to-print time; 58mm or 80mm. Verified physical completion time is separately recorded when ACK arrives. LABELS: repeated independent articles with brand/product/order/location/options/order time; no price, no cup ordinals. Default 40x30mm, bounded configurable dimensions. HTML encodes untrusted names/notes; printer calibration is NOT RUN.

## Database and configuration

Migration 20261114041318_KitchenBaristaWorkflow adds only kitchen.barista_acceptances and kitchen.kitchen_print_jobs (33 columns combined). Own-service FKs only, RESTRICT deletion, unique acceptance by ticket/cycle, actor/key hashes, and one non-reprint SLIP per ticket/cycle. See migrations/barista-workflow/README.md for guarded SQL, checksums, column grants and empty-only rollback.

Defaults: BaristaWorkflow:Enabled=false; WorkerEnabled=true (inactive while feature OFF); SlipWidthMm=80; LabelWidthMm=40; LabelHeightMm=30. Printer URL/key are intentionally absent. Database:MigrateOnStartup=false is retained. This task did not enable production flags, migrate operating databases, deploy, commit or push.

## Evidence and limitations

- Backend Services regression: 548 PASS / 51 SKIP. Skipped PostgreSQL/Redis opt-in tests are not claimed as executed by this regression.
- New focused backend tests: 9 workflow/CupV1/lease + 7 signed printer contract cases. EF InMemory and mocked service clients/event bus: not PostgreSQL atomicity proof.
- Isolated Gateway/PostgreSQL/Redis/signed Mock printer run: 24 grouped checks PASS / 0 BLOCKED including authoritative source-time/area checks; evidence is written to BARISTA_STACK_TEST_EVIDENCE.json.
- Flutter: 309 PASS / 1 SKIP, analyzer 0 issues on final regression. Barista-specific tests: 13 PASS. Mobile UI tests use HTTP mocks; they are not an Android hardware/full-stack Flutter E2E. Responsive 360/393/430 and no automatic printing on open/cancel are covered.
- Real printer, paper/label calibration, real bank QR, Web FE execution and Android device interaction: NOT RUN. Existing Bill reporting/shift reconciliation and CupV1 TTL/Manager/Dine-in policy blockers remain unchanged; see their existing documents.

Enablement checklist: approve migration/backup/role SQL for the actual deployment target; verify rollout with writes paused and feature OFF; configure and certify a durable real-device agent; verify physical output and unknown-result handling; validate legacy clients; resolve applicable CupV1 policy blockers before separately enabling CupV1. No production-ready claim is made.

## Test-run diagnostics (not counted as successful tests)

An interrupted stack run left only disposable labelled test containers; these were inspected and removed before the next nonce-isolated run. A preflight runner assertion initially included psql SET command text; quiet tuple output fixed the runner. The timestamp assertion initially compared a pre-storage .NET 100ns timestamp with PostgreSQL microseconds; it now compares the actual persisted Ordering GET value, without weakening source identity checks. A Services regression run encountered Windows FileSystemWatcher.StopRaisingEvents during unrelated Menu fixture disposal; rerun with DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE=false completed 548 PASS / 51 SKIP. This setting is scoped to the test command and does not alter production configuration. The early failed runs are not reported as recovery/printing PASS.

## Final validation and changed-file map

Final: Services 548 PASS / 51 SKIP (configuration hot-reload disabled for this command); Barista/adapter focused 16 PASS; Flutter 309 PASS / 1 SKIP, analyzer 0 issues; Gateway + PostgreSQL/Redis + signed Mock printer 24 grouped checks PASS, 0 BLOCKED. BARISTA_STACK_TEST_EVIDENCE.json confirms the preexisting container IDs/images/start times/status/restart counts were unchanged. The last run validated original order time against persisted Ordering GET and current Table area. SQL SHA256 and Git whitespace checks pass. All scratch run containers/network were removed; no operating volume touched.

| Files | Responsibility |
| --- | --- |
| Features/Barista/BaristaEndpoints.cs, BaristaOperations.cs, BaristaRecords.cs | Additive APIs, acceptance and print transaction/idempotency/audit, EF mappings. |
| Features/Barista/BarOrderDirectory.cs, BarLocationDirectory.cs | Authenticated read-only source-time and area contracts, no cross-service DB access. |
| Features/Barista/BarPrintDocument.cs, BarPrinter.cs, BarPrintWorker.cs | Escaped templates, verified ACK adapter, durable leased/fenced worker. |
| Features/KitchenEndpoints.cs, KitchenTicketTransitions.cs | Capability flag and acceptance/READY-only legacy preparation guards. |
| Features/CupServing/CupServingEndpoints.cs, CupServingUnitOfWork.cs | Pass feature configuration; require acceptance and persist preparation completion in existing CupV1 transaction. |
| Persistence/KitchenOperationsDbContext.cs, KitchenOperationsDbContextDesignFactory.cs, Migrations/20261114041318_KitchenBaristaWorkflow.cs + Designer + snapshot | Two additive Kitchen tables, offline scaffolding. |
| Program.cs, appsettings.json | Registration; default feature OFF and existing MigrateOnStartup=false retained. |
| tests/AICOS.Services.IntegrationTests/BaristaWorkflowTests.cs, BarPrintAgentTests.cs | Workflow/CupV1/lease and signed printer ACK contracts. |
| tests/AICOS.Billing.StackTools/Program.cs, scripts/tests/barista-stack-flow.mjs, session-billing-stack.mjs | Synthetic isolated PostgreSQL baseline/SQL/ACL/rollback/Mock-agent/Gateway validation. |
| docs/migrations/barista-workflow/, BARISTA_STACK_TEST_EVIDENCE.json, this report | Guarded SQL proposal, runbook, checksum and actual evidence. |

No Payment/Bill implementation file was changed. No operational DB migration, production feature activation, deployment, commit or push was performed.
