# Security workflow — 2026-11-13

## Dependency Review

User log: `Dependency review is not supported on this repository. Please ensure that Dependency graph is enabled`.

This is a repository capability/configuration failure, not a detected vulnerable package or evidence that the Backend build failed. An administrator must open https://github.com/KushinoAkira/AICOS-BE/settings/security_analysis and enable Dependency graph under the repository's code security settings. Private/internal repository eligibility additionally depends on the relevant GitHub Code Security entitlement. The exact current setting/plan has not been independently verified through an authenticated GitHub API.

After enabling, rerun Dependency review on the relevant pull request. No repository settings were changed by this task. Do not disable fail-on-severity or reinterpret a missing scan as PASS. Existing workflow already has job-level continue-on-error for this check; this task does not add another bypass.

## Separate CodeQL attachment

The attached CodeQL log is timestamped 2026-11-08, before the 2026-11-13 merge. Do not attribute it to the latest merge run without checking the Actions run SHA/date.

Evidence in that log:

- C# autobuild installs SDK 8.0.100 even though setup-dotnet already installed an 8.0.x SDK.
- Restore warnings resolve EF 9.0.22 against EFCore.NamingConventions 8.x constraints.
- CS1705: Identity assembly references System.Text.Json 9 while the test project references System.Text.Json 8.
- Autobuild exits 1 and CodeQL analysis cannot continue. This is a build/extraction failure, not a security finding.

Current source uses central EF package versions 8.0.31 and Npgsql provider 8.0.11. The old log does not establish that current source has the same dependency mismatch.

Change: `.github/workflows/security.yml` uses `build-mode: manual` for C# and runs explicit restore/build after CodeQL init, using the SDK selected by setup-dotnet. Python stays `none`. The entire solution is compiled for extraction; no test projects are silently excluded. Analyzer errors still fail the job. No package, application, database or migration changes.

```sh
dotnet --version
dotnet restore AICOS.sln
dotnet build AICOS.sln --configuration Release --no-restore --no-incremental
```

## Verification and limits

Local SDK 8.0.426: restore PASS; full Release nonincremental build PASS, 0 errors and 2 existing test warnings (CS0628/CS8620), elapsed 38.98 seconds. Diff whitespace check PASS. No local YAML/actionlint parser was available, so automated workflow-schema validation is NOT RUN. No application tests were rerun for this workflow-only change. This verifies build commands on Windows, not Linux CodeQL tracing/upload or GitHub Dependency Review. CodeQL itself and Dependency Review must be rerun on GitHub after the workflow update and repository setting change. No commit/push or GitHub security setting modification is part of this fix.

References:

## Follow-up execution authorized by user

User requested committing/pushing the workflow fix and assisting with the GitHub setting. Authenticated repository API GET confirms the repository is public and the current Git credential has `permissions.admin=false`. No token or credential is printed/saved. Dependency graph/alerts settings were not changed: repository admin action is still required. The existing Git identity can push; the two-file workflow/documentation fix is committed/pushed to main under the user's explicit request. Push starts a new Security workflow run; it does not prove CodeQL analysis or Dependency Review has passed. Actual commit/push and run status are reported separately after verification.

- https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/enabling-features-for-your-repository/managing-security-and-analysis-settings-for-your-repository
- https://github.com/actions/dependency-review-action
- https://docs.github.com/en/code-security/reference/code-scanning/codeql/build-options-for-compiled-languages
