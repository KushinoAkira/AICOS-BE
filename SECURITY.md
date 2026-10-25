# Security

## Report a vulnerability

Email the maintainers (GitHub profile of `@KushinoAkira`). Do not open a public issue that contains a live secret or an exploit.

We aim to acknowledge within 7 days and ship a fix or a public note within 30 days for confirmed high/critical issues.

## Secrets

Nothing secret has a default in `docker-compose.yml`. First run:

```
scripts/init-env.ps1   # or scripts/init-env.sh
```

That writes a git-ignored `.env`. Never commit it. AI keys (`GROQ_API_KEY`, `DEEPSEEK_API_KEY`) stay in `.env` or in GitHub Actions secrets — never in an image.

## What we run on every change

| Check | Where |
|---|---|
| Restore, build, test (+ Postgres / Redis) | `.github/workflows/ci.yml` |
| Gitleaks, CodeQL (C# + Python), NuGet audit, dependency review, Trivy FS | `.github/workflows/security.yml` |
| Image build, Trivy image (HIGH/CRITICAL fail), SBOM, provenance, GHCR push | `.github/workflows/docker-images.yml` |
| AI models lint, train, eval (safety gate), image | `.github/workflows/ai-models.yml` |
| Dependabot (NuGet, pip, Docker, Actions) | `.github/dependabot.yml` |

## Hardening overlay

```
docker compose -f docker-compose.yml -f compose.security.yml up -d
```

Drops Linux capabilities, forbids privilege escalation, binds debug ports to loopback, caps logs. Use it on any shared or demo host.

## Images

Pushed to `ghcr.io/<owner>/aicos-<service>` on `main` and `v*.*.*` tags, with SBOM and SLSA provenance. Pull requires `read:packages` on the repository.
