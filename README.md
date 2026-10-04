# AgenticShop

An educational .NET backend used to learn agentic software development, evolving
gradually from three simple HTTP services into a distributed e-commerce platform.

## Stack

C# · .NET 10 (LTS) · ASP.NET Core · PostgreSQL 17 · EF Core 10 · Docker

> The original brief specified .NET 11. As of September 2026 that is still
> pre-release (GA is November 2026), so the solution targets `net10.0`. Moving to
> 11 later is a `TargetFramework` change in `Directory.Build.props` plus package bumps.

## Architecture

Three independently deployed ASP.NET Core hosts. They communicate over HTTP only —
there is no broker, gateway, service discovery or shared library yet. Each is
deliberately introduced later, when it starts paying for itself. See **Roadmap**.

| Service | Port | Database | Role | Responsibility | Status |
|---|---|---|---|---|---|
| Catalog | 5081 | `agenticshop_catalog` | `catalog_svc` | Products, prices | **implemented** |
| Stock | 5082 | `agenticshop_stock` | `stock_svc` | On-hand quantities, reservations | **implemented** |
| Ordering | 5083 | `agenticshop_ordering` | `ordering_svc` | Orders, order lines, orchestration | **implemented** |

**The boundary that matters:** each service owns exactly one database and its own EF
Core migrations. Cross-service references are `Guid` ids resolved over HTTP — never
foreign keys, never joins. `OrderLine` snapshots `ProductName` and `UnitPrice` so a
later catalog edit cannot rewrite history. A foreign key *within* one service's database
is fine — Stock's `stock_reservations → stock_items` uses one, and Ordering's
`order_lines → orders` cascades because a line is part of the order aggregate.

That boundary is **enforced by PostgreSQL, not by convention**. Each service connects
with its own non-superuser role that owns its database and has had `CONNECT` revoked
for everyone else, so `catalog_svc` physically cannot open a connection to
`agenticshop_stock`. The `agenticshop` superuser exists only to bootstrap databases and
roles in `docker/postgres/init-dbs.sh`; no application uses it.

It is also enforced at build time: `Directory.Build.targets` fails the build if any
project under `src/` references another project under `src/`.

Prove the database half any time:

```powershell
docker compose exec db bash /usr/local/bin/verify-db-isolation
```

That runs 22 checks — own-database access, cross-database denial, absence of
`SUPERUSER`/`CREATEDB`/`CREATEROLE`, ownership, and the ability to create tables (which
EF Core migrations need) — and exits non-zero if any fails.

**Catalog is the reference implementation; Stock is the worked example of adapting it;
Ordering is the test of whether the conventions survive a service that orchestrates.**
Stock diverges where Catalog's conventions do not fit — no soft delete, no paging, no
money, and parallel rather than sequential concurrency tests. Ordering diverges where a
service with dependencies needs to — a 502 bucket, a `Clients/` folder, one orchestrating
collaborator, and an order that is written once and already terminal. Both divergence sets
are tabulated with reasons: `src/AgenticShop.Stock/docs/ARCHITECTURE.md` §8 and
`src/AgenticShop.Ordering/docs/ARCHITECTURE.md` §10.

## Layout

```
src/AgenticShop.Catalog/    products, prices
src/AgenticShop.Stock/      on-hand quantities, reservations
src/AgenticShop.Ordering/   orders, order lines, orchestration
src/AgenticShop.Shared/     cross-cutting infrastructure — not a service
├── Program.cs              composition root (services only)
├── Domain/                 entities and their invariants (no EF, no ASP.NET)
├── Data/                   DbContext + IEntityTypeConfiguration + Migrations
├── Contracts/              request/response records, implementing the shared IRequestContract
├── Endpoints/              minimal API route groups, one file per resource
├── Errors/                 the service's ProblemDetailsExceptionHandler subclass
└── Clients/                Ordering only: an interface + typed HttpClient per downstream service
tests/
├── AgenticShop.Shared.UnitTests/   the filter and the middleware, specified once
├── *.UnitTests/            domain rules, error classification — no I/O
└── *.IntegrationTests/     real API against a real PostgreSQL via Testcontainers
http/                       catalog.http, stock.http, ordering.http — runnable by hand
scripts/                    verification utilities (database isolation)
docker/postgres/            init-dbs.sh — creates the databases and owning roles
Directory.Build.props       TFM, analyzers, and the name of the one exempt shared project
Directory.Build.targets     the service-boundary build guard
Directory.Packages.props    central package versions
```

`AgenticShop.Shared` was extracted at the start of Phase 1. It holds the validation filter, the
correlation middleware, `IRequestContract` and the `ProblemDetailsExceptionHandler` base class —
cross-cutting infrastructure and nothing else. It has no endpoints, no `DbContext`, no domain and no
configuration, and it may not reference a service; the dependency only ever points one way. Before
that it was three near-identical copies, deliberately, and the reasoning for both halves of that
history is in `docs/DECISIONS.md`.

## Documentation

Global rules and design live at the root; service-specific material lives with the service.

| | Global | Catalog | Stock | Ordering |
|---|---|---|---|---|
| **Operational rules** | `AGENTS.md` | `src/AgenticShop.Catalog/AGENTS.md` | `src/AgenticShop.Stock/AGENTS.md` | `src/AgenticShop.Ordering/AGENTS.md` |
| **Architecture** | `docs/ARCHITECTURE.md` | `src/AgenticShop.Catalog/docs/ARCHITECTURE.md` | `src/AgenticShop.Stock/docs/ARCHITECTURE.md` | `src/AgenticShop.Ordering/docs/ARCHITECTURE.md` |
| **Decisions** | `docs/DECISIONS.md` | `src/AgenticShop.Catalog/docs/DECISIONS.md` | `src/AgenticShop.Stock/docs/DECISIONS.md` | `src/AgenticShop.Ordering/docs/DECISIONS.md` |
| **Known issues** | `docs/KNOWN-ISSUES.md` | `src/AgenticShop.Catalog/docs/KNOWN-ISSUES.md` | `src/AgenticShop.Stock/docs/KNOWN-ISSUES.md` | `src/AgenticShop.Ordering/docs/KNOWN-ISSUES.md` |

`docs/ROADMAP.md` holds the phase sequence, and records which phase owns each problem Phase 0
left open — the contention policy and the confirm-phase policy are decided in
`src/AgenticShop.Ordering/docs/DECISIONS.md`, and the roadmap names the phase that revisits them.

**Start with `AGENTS.md`** before changing code, then read the `AGENTS.md` of whichever service
you are touching. Global `docs/ARCHITECTURE.md` §4 documents the conventions every service
shares; the service files document only what differs.

## Prerequisites

- .NET SDK 10.0.400 (pinned in `global.json`)
- Docker Desktop, running — required for PostgreSQL and for the integration tests

## Getting started

```powershell
# 1. local config — defines the bootstrap superuser and the three service roles
copy .env.example .env

# 2. database (creates all three databases and their owning roles on first boot)
docker compose up -d db
docker compose ps                        # wait until healthy
docker compose exec db bash /usr/local/bin/verify-db-isolation

# 3. pinned local tools
dotnet tool restore

# 4. migrations (run as each service's own least-privileged role)
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet ef database update -p src/AgenticShop.Catalog  -s src/AgenticShop.Catalog
dotnet ef database update -p src/AgenticShop.Stock    -s src/AgenticShop.Stock
dotnet ef database update -p src/AgenticShop.Ordering -s src/AgenticShop.Ordering

# 5. run (three terminals, or `dotnet watch` each)
dotnet run --project src/AgenticShop.Catalog
dotnet run --project src/AgenticShop.Stock
dotnet run --project src/AgenticShop.Ordering
```

PostgreSQL is published on `127.0.0.1:5432` only. Binding it to `0.0.0.0` would expose
the bootstrap superuser to everything else on the local network.

The init script runs only when the data volume is empty. After changing it, recreate the
volume with `docker compose down -v` — this discards all local data.

| | Catalog | Stock | Ordering |
|---|---|---|---|
| API docs (Scalar) | <http://localhost:5081/scalar/v1> | <http://localhost:5082/scalar/v1> | <http://localhost:5083/scalar/v1> |
| OpenAPI document | <http://localhost:5081/openapi/v1.json> | <http://localhost:5082/openapi/v1.json> | <http://localhost:5083/openapi/v1.json> |
| Health | <http://localhost:5081/health> | <http://localhost:5082/health> | <http://localhost:5083/health> |
| Manual requests | `http/catalog.http` | `http/stock.http` | `http/ordering.http` |

Catalog and Stock are independent — either can run alone, and neither makes an outbound
call. **Ordering is not:** placing an order needs Catalog on 5081 and Stock on 5082. Its
integration tests need neither, because both clients are replaced at the interface seam;
`http/ordering.http` is the end-to-end path and drives all three in order.

Migrations are applied automatically on startup in `Development`, so step 4 is only
needed the first time or after changing the model.

## Build and test

```powershell
dotnet build
dotnet test                                               # everything — 426 tests
dotnet test tests/AgenticShop.Catalog.UnitTests           # fast, no Docker needed
dotnet test tests/AgenticShop.Stock.UnitTests             # fast, no Docker needed
dotnet test tests/AgenticShop.Ordering.UnitTests          # fast, no Docker needed
dotnet test tests/AgenticShop.Catalog.IntegrationTests    # needs Docker
dotnet test tests/AgenticShop.Stock.IntegrationTests      # needs Docker
dotnet test tests/AgenticShop.Ordering.IntegrationTests   # needs Docker
```

Integration tests start one real PostgreSQL container per collection and reset the
tables between tests. A real database is used instead of mocking EF Core because
mocks pass while the actual query fails — they hide migrations, unique indexes,
query filters, CHECK constraints and SQL translation bugs.

Stock also has parallel concurrency tests: a 40-way burst against 10 units must never
oversell, and a 20-way race to confirm one reservation must ship it exactly once.
Sequential state-machine tests cannot prove this — see `docs/ARCHITECTURE.md` §5.3.

`TreatWarningsAsErrors` is on, so a warning fails the build.

## Conventions

`AGENTS.md` is the operative rule set and `docs/ARCHITECTURE.md` §4 explains each rule with its
evidence. What follows is the shape of it, so the code reads consistently across three services.

- Minimal APIs with one endpoint-group file per resource; no controllers.
- **Every request DTO implements `IRequestContract` and every endpoint group registers
  `DataAnnotationValidationFilter`.** Minimal APIs do *not* validate body DTOs automatically —
  that is an MVC `[ApiController]` behaviour — so without the filter the attributes in
  `Contracts/` are decorative and an over-length field reaches PostgreSQL and surfaces as a
  `500` instead of a `400`. That was a real bug; it is now regression-tested. The filter
  recurses into nested objects *and* collections and reports the full member path
  (`Lines[0].Quantity`), because `Validator.TryValidateObject` does not cascade on its own.
  `RequestContractCoverageTests` fails the build if a `*Request` DTO forgets the marker.
- Entities own their invariants (`private set`, static `Create`, explicit mutation methods) —
  those guard semantics. Length and format limits on the wire live in `Contracts/` as
  DataAnnotations — those guard the boundary. The overlap is intentional. Guards run before any
  mutation, so a rejected call leaves the entity untouched.
- Derived values are computed properties, never stored — Stock's
  `Available = QuantityOnHand - Reserved` — and marked `builder.Ignore(...)`.
- **Every entity carries an `xmin` concurrency token**, declared as a `uint` shadow property that
  Npgsql maps to the system column, so it emits no DDL. Without it two overlapping writes both
  succeed and the second silently discards the first: on `Product` that is a lost price edit, on
  `StockItem` it is overselling.
- Multi-entity writes commit in a **single `SaveChangesAsync`**, so EF wraps them in one
  transaction — the property Phase 2's outbox will rely on.
- **Let the database reject duplicates rather than pre-checking.** `SELECT`-then-`INSERT` has a
  race window under retry; a unique index does not.
- Errors are `ProblemDetails` with a `correlationId` extension, produced by `IExceptionHandler`,
  under four rules: **a client error is never a 5xx** · **a server fault is never a 4xx** ·
  **client cancellation is not an error** · **no exception message reaches the client** — those
  carry internal parameter names and are formatted with the server's culture, so on a
  Turkish-locale host `-5.00` renders as `-5,00`. Domain conflicts build their message from typed
  exception properties instead, and unique violations map to `409` keyed on the *constraint name*.
  Severity is decided by the handler, not by EF Core, so
  `Microsoft.EntityFrameworkCore.Update` is silenced in `appsettings.json`.
- Required configuration is validated at startup: a missing `ConnectionStrings:<Service>` throws
  during host construction rather than producing a service that boots, reports healthy, and then
  500s on every request. The value is read *lazily* inside the `AddDbContext` callback and
  validated *after* `Build()`, because reading it eagerly freezes it before a host applies its
  configuration overrides — which once caused the integration tests to run silently against a
  developer's own database.
- `X-Correlation-Id` is accepted on the way in and always echoed, so one request can be traced
  across all three services. The value is untrusted — it ends up in response headers, the
  ProblemDetails body and every log line — so an over-long or malformed one is *replaced* with a
  freshly minted id rather than echoed, and rather than rejecting the request. Catalog and Stock
  are inbound-only; Ordering forwards it on every outbound call through a `DelegatingHandler`.
- **Catalog-only, not house style:** soft delete (`IsActive` + `HasQueryFilter` +
  `includeInactive`), offset paging, and `numeric(18,2)` money with a `Currency` column. Stock has
  none of these — a silently filtered stock row would read as "no stock", which is more dangerous
  than a deleted product — and neither does Ordering.
- A contract's limits must match the columns they write to. `ContractSchemaAlignmentTests` reads
  them from the EF model and fails on drift. **Each service derives its own**, because a copied
  guard would have matched nothing in Stock and passed without proving anything; what each covers
  is tabulated in `docs/ARCHITECTURE.md` §5.5.
- Postgres identifiers are `snake_case` via `EFCore.NamingConventions`. This also renames
  EF's `__EFMigrationsHistory` columns to `migration_id` / `product_version`.

**Service boundaries are enforced at build time.** `Directory.Build.targets` fails the
build if any project under `src/` holds a `ProjectReference` to another project under
`src/`. Services communicate over HTTP only; a compile-time reference would make the
solution a distributed monolith in all but name and remove the failure modes the later
phases exist to teach. All three services inherit the rule, and each also has a runtime
guard for the dependency a `ProjectReference` is not — Stock's `LeafServiceBoundaryTests`
asserts it registers no `HttpClient` at all, Ordering's `ServiceBoundaryTests` asserts the
inverse and that no other `AgenticShop.*` assembly is even loaded. Test projects are exempt.

Note that `dotnet run` always applies `launchSettings.json`, whose profile pins
`ASPNETCORE_ENVIRONMENT=Development` *and* the fixed port. There is no `--environment`
switch that overrides the first, and `--no-launch-profile` removes both — so the service
then starts in Production, does not load `appsettings.Development.json`, and fails fast on
the missing connection string. That failure is the configuration guard working, not a
broken service. Because startup runs `MigrateAsync()` in Development, the service also
fails fast if PostgreSQL is not reachable — start the database first.

## Credentials

Every credential in this repository is a **published placeholder for a local, disposable
database** — not a secret, and never to be reused.

| Where | What | Why it is safe |
|---|---|---|
| `.env.example` | bootstrap superuser + three service-role passwords | copied to `.env`, which is gitignored; values are placeholders |
| `appsettings.Development.json` | `catalog_svc`, `stock_svc` and `ordering_svc` connection strings | Development-only files; container is loopback-bound and holds disposable data |
| `CatalogApiFixture`, `StockApiFixture`, `OrderingApiFixture` | Testcontainers credentials | throwaway containers that exist only for the duration of a test run |

The guards that keep this true:

- `docker-compose.yml` publishes PostgreSQL on `127.0.0.1` only.
- Applications connect with least-privilege roles; the superuser exists only to bootstrap.
- No `appsettings.json` (non-Development) contains a connection string, and startup fails
  if one is absent — so a real environment must supply `ConnectionStrings__<Service>`
  through the environment or a secret store. Ordering's two downstream base URLs follow the
  same rule and are likewise absent from non-Development configuration.
- All three `ComposeConfigurationTests` fail if a literal password appears in the compose file or
  if the port binding stops being loopback-only.

To use different values locally, edit `.env` and run
`dotnet user-secrets set "ConnectionStrings:Catalog" "..."` (or `:Stock`, `:Ordering`) against the
`UserSecretsId` already declared in each service project. No secret-management system is
introduced until there is a deployment to manage secrets for.

## Roadmap

Each phase adds one capability, and only once the previous phase makes it necessary.

**Phase 0 — Foundation** *(complete)*
Three hosts, synchronous HTTP, one database per service, Testcontainers. All three services
are implemented and verified: at the close of Phase 0, 426 tests, a clean build under
`TreatWarningsAsErrors`, database isolation 22/22, and the three-host path driven by hand
through `http/ordering.http`. **The suite is now at 503.**

**Phase 1 — Reliability and observability** *(in progress)*
Shared infrastructure library — **done** · idempotency keys on `POST /orders` — **done** ·
retry/circuit-breaker/timeout policies on Ordering's typed clients — **done** · Serilog structured
logging — **done in all three services** · OpenTelemetry distributed tracing — **done in all three** ·
dependency-aware health checks — **done** · Dockerfiles and a `full` compose profile · CI. Retry is
driven by a measured problem:
a 40-way burst against 10 units of stock held only 4, because `xmin` conflicts are not retried.
Idempotency keys came first, because retrying without them double-reserves stock — and they closed
the largest residual the repository had, since a retried `POST /orders` used to create a second order
and a second set of holds. Note that the HTTP retry now in place does **not** address that burst:
Stock's conflict 409 is a normal 4xx and is not retried. Reserve is also exempt from retry, because a
retried reserve whose first attempt committed is answered 409 and would be misread as out-of-stock —
see `src/AgenticShop.Ordering/docs/DECISIONS.md` → O18.

**Phase 2 — Asynchronous messaging** ← the inflection point
RabbitMQ · replace synchronous reservation with `OrderPlaced` / `StockReserved`
events · outbox pattern · saga with compensation · dead-letter queues.

**Phase 3 — Data and scale**
Redis caching and idempotency store · reservation-expiry worker · read models ·
consumer-driven contract tests.

**Phase 4 — Platform**
YARP gateway · Kubernetes · service discovery and mTLS · load and chaos testing.

The order is not arbitrary. Retrying without idempotency keys double-reserves stock;
an outbox without a broker to publish to is dead weight; CQRS without read pressure
is ceremony. `docs/ROADMAP.md` is authoritative for detail.
