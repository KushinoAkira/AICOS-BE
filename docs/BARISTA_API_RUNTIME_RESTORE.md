# Local Barista API runtime restore — 2026-11-17

User reported Barista saying Backend unavailable on the real phone and requested a fix.

## Diagnosis

- Gateway/network were reachable; authenticated Barista tickets GET returned 503 because the currently running Kitchen had BaristaWorkflow:Enabled=false.
- The earlier local Barista migration already exists: cluster 7703616372368646182, aicos_kitchen.kitchen, history 20261114041318_KitchenBaristaWorkflow. Required tables and column-level privileges were present. Do not repeat the one-shot migration deployment script.
- The local rollout was previously approved and executed on 2026-11-14; later runtime smoke configuration had restored all feature flags OFF.

## Executed repair

Used scripts/Restore-LocalBaristaApi.mjs to recreate only Kitchen on the exact existing image hash, copying credentials/JWT/ports/networks/config in memory. Changed only BaristaWorkflow__Enabled=true. Kept WorkerEnabled=false, MigrateOnStartup=false, SessionBilling=false, CupServing=false and KitchenAdmission=false. No build, migration, SQL write, seed, credential/JWT change, receiving/completing ticket, print job or payment API was executed.

Worker remains OFF because a physical printer was excluded from the current work. Opening/refreshing Barista does not print or fabricate printed status. User acceptance can still create the normal queued print record; it is not evidence of a physical print.

## Evidence

BARISTA_API_RUNTIME_RESTORE_RESULT.json: PASS. Kitchen readiness/Gateway metadata 200; three Barista GET routes return 401 for absent/invalid JWT, not disabled-feature 503. Complete Kitchen table row fingerprints before/after match. All other container ID/image/start/status/restarts match. Only the old stopped Kitchen container, which had no mounts, was removed after verification; no volume was touched.

The actual phone's authenticated list/accept/start/complete flow was not triggered by the agent. User should refresh Barista using their existing login. A physical printer was not tested or enabled.

Local docker-compose.main-runtime.yml now explicitly preserves Barista API ON and printer worker OFF; base production Compose defaults are unchanged. Financial Bill activation remains separate: both SessionBilling flags OFF and no environment machine key observed; no financial feature was enabled by this fix.

No commit/push.