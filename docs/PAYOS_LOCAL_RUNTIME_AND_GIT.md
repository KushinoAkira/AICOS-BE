# Payments local update, Git and cache cleanup — 20/11/2026

## Completed Git checkpoint

Source merge commit **`427335de5d6d2a2bbf37839729422c00929d7686`** was pushed to `origin/be/payment-main-integration-20261119`; `ls-remote` matched local HEAD. Mobile source commit **`71adebe6ca102ed562d6dd3c891aaa4da296e9e4`** was pushed fast-forward to `origin/main`, also verified against remote. Backend main was not pushed or merged. Both reviewed worktrees were clean after source commits. This documentation checkpoint follows those source commits; it does not imply public activation or remote CI completion.

User requested continuing the local update, handling Git and reducing disk usage. User authorized main only where safe, otherwise a separate branch for later review. No public tunnel, real-provider configuration or financial feature activation was authorized/performed here.

## Local Payments result: PASS

- Image built from the tested integration worktree: `aicos/payment:payos-staff-readonly-20261120`, ID `sha256:e5b000fe72658203a23a92e380591f43d721acb0e8803ec2b784952df19b8fee`.
- Replaced only Payments. New container `f84c1a138acd27ef2de7ad8d5ab30dbd2c0440670dc7ddcf834e8fb4af30af3e`. Previous container `b48d6cec0579fe48e2fd413f3e83cf26a4cd756c409546727450aa97559eaf21` retained stopped as `aicos-main-payment-before-payos-b48d6cec`; original image remains.
- Credentials/environment, ports and network preserved byte-for-byte where compared. MigrateOnStartup=false, expiry worker OFF, Cash collection dispatcher OFF; existing SessionBilling dispatcher remains ON with preflight pending PAID events exactly 0. PayOS staff flag remains OFF by default, provider rows exactly 0. No new credentials seeded/collected.
- Health live/ready and Gateway metadata 200; missing JWT on Bill API 401; direct/Gateway return probe without orderCode 302, `X-Aicos-Return-Mode: read-only`, no-store. No login, forged JWT or financial/business mutation API used.
- Cluster `7703616372368646182`; fingerprints of **10 logical service databases** identical before/after (including migration-history rows). Sixteen other container IDs/images/start times/status/restart counts unchanged. These operational databases are distinct from scratch PostgreSQL used by integration tests.
- Fresh `aicos_payment` custom-format backup stored outside Git under the current user's `%LOCALAPPDATA%/AICOS/backups/`; checksum/path in `PAYOS_LOCAL_RUNTIME_RESULT.json`. pg_restore TOC verified. **Fresh restore drill NOT RUN in this gate**; do not call TOC validation a restore test.
- Script `scripts/Apply-LocalPayOsPayment.mjs` defaults to read-only `--check`, guarded `--apply` is single-use against the pinned original container/image; a repeated apply fails preflight. It keeps secrets in memory, suppresses raw errors and never runs SQL migration/DML/automatic rollback. Startup mutation workers are audited before apply; a pending PAID outbox or active writer stops it.

## Verification

- Full Backend regression for the final source: **1,030 PASS, 69 SKIP, 0 FAIL**, process exit 0: BuildingBlocks 15, Identity 210, Integration 755/69, Smoke 50. Infrastructure skips are not PostgreSQL/Redis success evidence.
- PostgreSQL scratch **25 PASS/0 SKIP** and Nginx transport **9/9 PASS** remain their separate earlier evidence; no live bank/Gateway financial E2E claim.
- Mobile fresh focused payOS/cancellation/Bill/sandbox: **40 PASS/0 SKIP**, analyzer **0 issues**. Prior full Flutter **436 PASS/9 SKIP** is historical for the same Dart source. Cleanup invalidated only generated build/cache files; dependencies/tests regenerate them normally.
- Original 50-file cancellation WIP hashes remain unchanged in the preserved worktree. Original Backend root worktree and its unrelated Item Transfer WIP were not reset/stashed/moved/staged.

## Git decision

- Mobile local and remote main were identical at `4b77d34` before commit, so the tested Mobile work can be committed/pushed to main without force/rebase.
- Backend origin/main remains `de7e011`; the current worktree contains a pending two-parent merge plus local gated features/proposals. Publish **`be/payment-main-integration-20261119`**, not Backend main. Completing a source merge commit here does not apply its migrations or activate its features.
- All reviewed source/tests/docs needed by the combined tree are included; generated build files, dump backups, DPAPI secrets and local environment files are excluded. Pending cancellation/discount/Apriori/other upstream migrations still require their own approval.
- The staged upstream AI document contains inherited whitespace warnings and two reviewed cancellation SQL files contain an extra blank EOF line. Their reviewed SQL/EF checksums are preserved; unrelated source was not reformatted to hide warnings. Local payOS changes pass diff checks.
- Final commit hashes/push status are reported from Git after execution, not inferred from this plan. Do not auto-merge this Backend branch or overwrite original working changes.

## Cleanup

- Removed approximately **2,676.9 MiB** (~2.61 GiB) of generated files: Flutter `build`, `.dart_tool/flutter_build`, `.dart_tool/hooks_runner`, and **114** ignored .NET bin/obj directories in four inactive test/hotfix worktrees. Each root/target was verified; tracked files and reparse targets were rejected. Windows long paths required .NET's explicit long-path deletion for the build cache; no mixed-shell deletion/global Git safety exception was used.
- Fresh focused Flutter tests recreated some cache; reclaimed numbers describe removed artifacts, not a guaranteed host free-space delta after all tests.
- After tests/push, removed another **32** ignored bin/obj directories from the active integration worktree, approximately **554.7 MiB**. Total generated artifacts removed: approximately **3,231.6 MiB (~3.16 GiB)**. These paths were rechecked against the exact worktree root, Git tracked/ignore rules and reparse-point guards. No source changes resulted. Build outputs will regenerate on the next .NET/Flutter build.
- Kept source, worktrees/branches, Git history, package caches, database volumes, backups, existing operational/rollback images and running services. Shared Docker BuildKit cache was inspected but not globally pruned because ownership could not be proven from its descriptions. No `system prune`, volume deletion or Docker reset.

## Next activation gate

The safe local browser capability now exists. Still required before real money: explicitly approved public tunnel/allowlist, tenant/receiver/channel verification, hidden payOS keys/provider setup, exact HTTPS callback/return/result URLs, and an approved small-value transfer with bank → Payment/Bill → outbox → Reporting reconciliation. No tunnel was installed/opened and no bank request sent in this update.

Keep the new callback/query code and old keys whenever real attempts exist. Do not downgrade an image or delete an intent to resolve an outstanding receipt. The old S01 legacy Cash reconciliation remains separate; no old money was collected twice or silently moved.
