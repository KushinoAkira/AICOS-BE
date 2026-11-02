# Legacy AICOS containers — 2026-11-02

Docker Engine: 29.7.2, reachable.

## Result

All 16 legacy AICOS containers were already exited when inspected after the owner enabled Docker. No stop command was needed or executed. All three attached named data volumes were found: aicos-be_redis-data, aicos-gate-d_pg18-data and aicos-gate-d_redis-data.

The 13 old Gate C containers reported exit code 0. The three Gate D test containers reported exit code 255; this check did not investigate the historical shutdown cause and does not classify their application health. No container was started to test it.

No container, network or volume was removed, renamed or recreated. No restart policy was changed. Windows PostgreSQL, aicos_db, other databases and credentials were not accessed or changed. Unrelated containers were left alone. No migration/seed, commit or push.

## Important for the new main deployment

The old containers still reserve their names, and the old Compose project is aicos-be, matching the default project derived from this directory. Running the new main Compose without isolation could reuse or recreate legacy containers. Stopping a container does not release its name.

Before starting main, prepare a distinct Compose project, distinct container names, distinct volumes and non-conflicting host ports. Preserve the main source's database-per-service architecture. Do not use down -v, prune, old volumes or the old Windows database to initialize the main stack. This report does not approve automatic startup, migration or seed.

## Verified container inventory

| Name | State | Exit | Project |
| --- | --- | ---: | --- |
| aicos-gateway | exited | 0 | aicos-be |
| aicos-ai | exited | 0 | aicos-be |
| aicos-customer | exited | 0 | aicos-be |
| aicos-reporting | exited | 0 | aicos-be |
| aicos-workforce | exited | 0 | aicos-be |
| aicos-inventory | exited | 0 | aicos-be |
| aicos-kitchen | exited | 0 | aicos-be |
| aicos-payment | exited | 0 | aicos-be |
| aicos-order | exited | 0 | aicos-be |
| aicos-table | exited | 0 | aicos-be |
| aicos-menu | exited | 0 | aicos-be |
| aicos-identity | exited | 0 | aicos-be |
| aicos-redis | exited | 0 | aicos-be |
| aicos-gate-d-identity | exited | 255 | aicos-gate-d |
| aicos-gate-d-postgres | exited | 255 | aicos-gate-d |
| aicos-gate-d-redis | exited | 255 | aicos-gate-d |
