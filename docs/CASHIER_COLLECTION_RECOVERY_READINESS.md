# Gate B recovery tooling — prepared, NOT executed locally

Date: 2026-11-16. Scope: implement/test a one-shot administrative runner after Gate A configuration PASS. No operating collection, Reporting fact, app account/JWT, key, schema, permission or container was changed by this preparation. Gate B execution still requires explicit financial approval.

## Code and scope

- tests/AICOS.Billing.StackTools/ApprovedCollectionRecovery.cs: mode recover-approved-collections. Reuses CashierLedger.CaptureAsync, existing payment/order advisory-lock key and the production CollectionReceipt/ACK protocol. It intentionally does not call the normal broad CollectionDispatcher.RunAsync. Normal service source, public APIs, hosted workers, images and Web FE contracts are unchanged.
- tests/AICOS.Billing.StackTools/CollectionRecoveryProbe.cs: offline full-schema export plus synthetic fixture/real HTTP receiver test modes. Fixture and fault modes reject every operating database name/cluster; none is registered in a deployed service.
- Program.cs: two mode dispatch lines, no automatic invocation.
- scripts/tests/approved-collection-recovery.mjs: isolated PostgreSQL + real Kestrel HTTP through production MapCollections receiver, unique internal network/tmpfs/no host port, restricted service credentials and synthetic fixtures. Existing cached .NET application image is used only as a runtime with a read-only published-tool mount and overridden entrypoint; it never starts the Payments application.

No migration was added/edited. Existing EF scripts were exported offline and applied only to two fresh scratch databases.

## Safety contract

- Explicit AICOS_RECOVERY_EXECUTION_APPROVED=true and exact plan SHA256 required. No operating command was invoked with this approval in preparation.
- Main target only aicos_payment/payment, cluster 7703616372368646182, authenticated current_user=payment_svc. The tool independently queries system_identifier/database/current_user before any write. Reporting URL restricted to internal reporting:8080, redirects disabled; existing machine key supplied only through process environment.
- Exactly two distinct EventIds/OrderIds, one tenant/branch, Cash only, total 258.000đ, UTC original timestamps, valid original references/actors, Recovered=true, BillId/ShiftId=null. No QR, altered source/refund policy, shift assignment, fake Bill or new payment.
- Acquire existing order advisory locks in deterministic order and lock original Payment rows; re-read the proof under one short capture transaction. Refuse any changed source, new pending refund, existing Bill association, unexpected missing payment/outbox or nonempty cashier-shift baseline before capture. Both proofs validated before CaptureAsync/SaveChanges/COMMIT.
- Existing payload and physical ledger fields must match the approved identity. Replays do not silently overwrite receipts. Same EventId makes source/receiver deduplication stable.
- Commit capture before HTTP; no PostgreSQL transaction spans network delivery. Only exact HTTP **200** plus the matching EventId ACK marks DeliveredAtUtc. Timeout/lost/202/wrong ACK/auth failure retains durable pending proof and stops. No automatic retry loop, delete, rollback or broad scan.
- Errors show phase and whether capture committed, suppressing raw secrets/error dumps. A post-capture failure can leave two legitimate outbox rows and partial inbox delivery; this is not an all-or-nothing distributed transaction. Stop, inspect, then retry the same approved plan without deleting proof.

## Actual tests

**19 PostgreSQL/real-HTTP groups PASS, 0 FAIL**: target/role/approval/hash/source guards; unexpected third source; normal exact recovery; full retry; two concurrent processes; actual process exit after capture commit; lost ACK after receiver commit; receiver restart; wrong EventId ACK; HTTP 202 rejection; wrong machine key; legacy Cash-only/recovered/no-shift/unique identity policy; modified existing payload; real role denial of DELETE and original money UPDATE; refund drift; original container identity/state unchanged. Original paid timestamps include microseconds and remain exact. Sources/amounts/actors are synthetic; equal amounts do not represent operating recovery.

**18 related regression tests PASS, 0 FAIL, 0 SKIP**: CashierShiftTests/CashPrecheckTests, HTTP harness/in-memory/test doubles. Do not report these as PostgreSQL or real bank tests.

Build/publish, C# whitespace formatter, Node syntax and diff whitespace PASS. The first scratch setup attempt incorrectly treated an EXPOSEd-but-unpublished PostgreSQL port as a host binding and stopped before schemas/fixtures. Guard was corrected to verify HostConfig.PortBindings and null/empty NetworkSettings binding values. Final tests kept the no-published-port requirement; the failed attempt is not counted as PASS.

Redis **N/A**: this durable recovery path uses PostgreSQL/HTTP and never relies on Redis Pub/Sub. Redis restart resilience of unrelated flows is not claimed. Gateway/Flutter authenticated E2E, normal dispatcher operation, SessionBilling and real bank/provider tests are **NOT RUN** here.

## Reviewed operating plan (private, awaiting approval)

Plan contains the exact two reviewed legacy Cash payment/Order IDs, total **258.000đ** (208.000đ + 50.000đ), immutable original actor/reference and paid times on November 4/6. IDs/full payload are stored only in COLLECTION_RECOVERY_PLAN.json in the existing restricted private directory; public summary is CASHIER_COLLECTION_RECOVERY_PLAN.json.

SHA256: **91bd27b47249a772c3071acb32f07ccbc1e8b4de22098b896ebf2dd825a858b9**.

One source session is OPEN with three Orders, one is CLOSED/Paid with one Order. Recovery only records the old money proof, preserving OrderId instead of inventing an aggregate Bill. It must not close/open either session, mark other Orders paid, charge again or allocate old Cash to a new drawer. The OPEN legacy session's transition to SessionBilling remains a separate blocker, protected by billing.legacy_payment guards.

Preserve exact original microseconds; never pass PaidAtUtc through JavaScript Date, which truncates precision. Old receipts stay on their original branch/date and do not increase today's collection. Do not add collection_facts totals to OrderFacts revenue or invent net/refund allocation.

## Exact future command shape — review only

After Gate B approval and fresh preflight, load credentials from the unchanged Payments container and the existing Collections DPAPI artifact in memory; verify key equality with Reporting. Do not print inspect/env/config/decrypted values or rotate keys. Publish this reviewed StackTools revision into a private/temp artifact directory. Mount only that artifact read-only and the one approved plan file read-only, not the private backup directory or source workspace.

Inside the existing verified shared Docker network:

    docker run --rm --network <verified-main-network> --entrypoint dotnet \
      --mount type=bind,source=<published-tool>,target=/tool,readonly \
      --mount type=bind,source=<private-plan-file>,target=/approved-plan.json,readonly \
      -e AICOS_RECOVERY_EXECUTION_APPROVED -e AICOS_RECOVERY_PLAN_SHA256 \
      -e AICOS_RECOVERY_CONNECTION -e AICOS_REPORTING_BASE_URL \
      -e AICOS_COLLECTIONS_MACHINE_KEY \
      aicos-main/payment:dashboard-396b7e8596bb1d993272d6fe7c0620b6c62db7c4 \
      /tool/AICOS.Billing.StackTools.dll recover-approved-collections /approved-plan.json

Environment values must come from the approved private inputs, never command-line literals: connection points to the actual Docker Payments DB using payment_svc; Reporting base is http://reporting:8080; key is the unchanged Gate A key; plan hash is the exact value above; the approval value is true only after user confirmation. Do not set test crash/fault variables on main. These placeholders are deliberately not an executable host recipe without preflight-resolved network/artifact/private paths; no nonexistent wrapper or endpoint is claimed.

Preflight must recheck backup SHA/restore evidence and its data coverage, exact current cluster/containers/images/service-role rights, dispatcher OFF, client mutation pause/no writers, private plan hash, exactly the reviewed source proofs and Order/session relationships, zero outbox/facts/shifts or matching same-plan partial-retry state, Reporting health/key match and correct target role/DB. Stop on drift; do not alter source data/ACL to make a check pass. Capture fresh private before/after financial/table fingerprints. Gate A approval alone does not authorize this command.

Expected approved delta: two payment.collection_outbox rows, two reporting.collection_facts rows, stable matching ACKs/delivery timestamps. Original Payments/Orders/Bills/sessions/shifts remain unchanged; exact source/outbox/inbox identities, original timestamps and sum 258.000đ agree. Retry may finish existing same-plan rows, not add two more. Stop on error, retain durable proof; never delete receipts or restore pre-gate DBs automatically.

## Status and next approval

Tooling **READY FOR REVIEW**, not production-ready. Operating recovery **NOT RUN / awaiting Gate B**. User must approve legacy OrderId-based historical collection treatment and the exact plan/write delta. Normal worker stays OFF, SessionBilling/expiry/recovery/print flags retain prior state, and no new operating payment/shift/UI test is part of Gate B unless separately approved. No source integration code is left masquerading as an executed financial operation.

Evidence: CASHIER_COLLECTION_RECOVERY_TEST_RESULT.json, CASHIER_COLLECTION_RECOVERY_PLAN.json. Existing partial V2 code in the original Backend/Mobile remains untouched.

## Gate B executed — 2026-11-16

Separately approved exact two Cash historical sources/258.000đ synchronized once: 2 outbox, 2 Reporting facts, 2 stable ACKs, ShiftId=null, original Payment/Order/Bill/session data unchanged. Fresh two-DB backup/restore 5/5 PASS. See CASHIER_COLLECTION_RECOVERY_EXECUTION.md and result JSON. Only four required dependencies started after the entire stack had been found stopped; nine other containers including Gateway remain stopped. Normal dispatcher and SessionBilling remain OFF; Mobile/authenticated business E2E not run. Earlier awaiting-approval sections describe preparation, superseded by this execution record.
