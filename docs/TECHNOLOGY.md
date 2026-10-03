# Technology

**Global** stack: what the repository builds on, where each version is pinned, and where the
non-obvious choices are argued.

The rules an agent must follow — `TreatWarningsAsErrors`, central package management, the TFM, the
LF requirement on `*.sh`, the localised CLI output — live in `AGENTS.md` §4, which is loaded on
every request. This file is the reference behind them and deliberately repeats none of them.
Rationale for individual choices is in `DECISIONS.md`; the conventions they imply in
`ARCHITECTURE.md`.

Everything here describes the verified current state.

---

## 1. The stack

| Concern | Choice | Version |
|---|---|---|
| SDK | pinned in `global.json`, `rollForward: latestFeature` | 10.0.400 |
| TFM | set once in `Directory.Build.props` | `net10.0` |
| Web | ASP.NET Core **Minimal APIs** — no controllers | 10.0.12 |
| Database | PostgreSQL, image `postgres:17-alpine` | 17 |
| ORM / provider | EF Core / `Npgsql.EntityFrameworkCore.PostgreSQL` | 10.0.12 / 10.0.3 |
| Naming | `EFCore.NamingConventions` → snake_case | 10.0.1 |
| API docs | `Microsoft.AspNetCore.OpenApi` + `Scalar.AspNetCore` | 10.0.12 / 2.17.8 |
| Resilience | `Microsoft.Extensions.Http.Resilience` — Ordering only, the sole service with outbound calls | 10.0.0 |
| Logging | `Serilog.AspNetCore` — Catalog only; Stock and Ordering still use the built-in providers | 10.0.0 |
| Containers | Docker / Compose | 29.8.0 / 5.5.1 |
| Test DB / host | `Testcontainers.PostgreSql` / `Mvc.Testing` | 4.15.0 / 10.0.12 |
| Tests | xunit + FluentAssertions | 2.9.3 / 8.11.0 |
| Solution | `.slnx` | — |

## 2. Where each version is actually pinned

**The table above is a reading aid, not the source of truth.** Every version is pinned in a build
file; when the two disagree, the build file is right and this document is stale.

| Pinned in | Holds |
|---|---|
| `global.json` | the SDK version and its roll-forward policy |
| `Directory.Build.props` | the TFM, `TreatWarningsAsErrors`, shared build properties |
| `Directory.Packages.props` | every `PackageReference` version, centrally — a project must omit `Version` |
| `dotnet-tools.json` | `dotnet-ef` |
| `docker-compose.yml` | the `postgres:17-alpine` image |

Change a version in its pinning file and update §1 in the same commit.

## 3. Where the choices are argued

`DECISIONS.md` carries the reasoning. The entries that settle a row of §1 are: "Target `net10.0`,
not `net11.0`" · "Minimal APIs, not controllers" · "`.slnx` rather than `.sln`" · "Central package
management with transitive pinning" · "`dotnet-tools.json` at the repository root" · "One
PostgreSQL container hosting three databases" · "Testcontainers, never a mocked `DbContext`" ·
"An endpoint filter, not FluentValidation" · "Serilog, adopted in Catalog first", which supersedes
"Built-in logging, no Serilog yet".

A decision taken while building one service lives in that service's `docs/DECISIONS.md` instead. The
resilience row is such a case: `src/AgenticShop.Ordering/docs/DECISIONS.md` → **O18**, which covers
the policy values, why retry is enabled per request rather than per client, and why creating a
reservation is the one call exempt from it.
