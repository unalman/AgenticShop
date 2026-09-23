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
| Ordering | 5083 | `agenticshop_ordering` | `ordering_svc` | Orders, order lines, orchestration | not started |

**The boundary that matters:** each service owns exactly one database and its own EF
Core migrations. Cross-service references are `Guid` ids resolved over HTTP — never
foreign keys, never joins. `OrderLine` will snapshot `ProductName` and `UnitPrice` so a
later catalog edit cannot rewrite history. A foreign key *within* one service's database
is fine — Stock's `stock_reservations → stock_items` uses one.

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

**Catalog is the reference implementation; Stock is the worked example of adapting it.**
Stock deliberately diverges where Catalog's conventions do not fit — no soft delete, no
paging, no money, and parallel rather than sequential concurrency tests. Ordering is not
started. See `docs/ARCHITECTURE.md` §5.8 for the divergences and why each was right.

## Layout

```
src/AgenticShop.Catalog/    products, prices
src/AgenticShop.Stock/      on-hand quantities, reservations
├── Program.cs              composition root
├── Domain/                 entities and their invariants (no EF, no ASP.NET)
├── Data/                   DbContext + IEntityTypeConfiguration + Migrations
├── Contracts/              request/response records + the IRequestContract marker
├── Endpoints/              minimal API route groups, one file per resource
├── Errors/                 IExceptionHandler → RFC 9457 ProblemDetails
├── Validation/             endpoint filter that runs DataAnnotations
└── Middleware/             correlation id
tests/
├── *.UnitTests/            domain rules, filter, error classification — no I/O
└── *.IntegrationTests/     real API against a real PostgreSQL via Testcontainers
http/                       catalog.http, stock.http — runnable by hand
scripts/                    verification utilities (database isolation)
docker/postgres/            init-dbs.sh — creates the databases and owning roles
Directory.Build.props       TFM and analyzers, applied to every project
Directory.Build.targets     the service-boundary build guard
Directory.Packages.props    central package versions
```

There is no `AgenticShop.Shared` project. Every service reports errors as standard
`ProblemDetails`, which is contract enough without a shared assembly to drift. The
validation filter, exception handler and correlation middleware are copied per service on
purpose; that duplication has a trigger, recorded in `docs/DECISIONS.md`.

## Documentation

| File | For |
|---|---|
| `AGENTS.md` | Operational rules — read this before changing code |
| `docs/ARCHITECTURE.md` | Design in depth; the Catalog (§4) and Stock (§5) references; testing architecture (§6) |
| `docs/DECISIONS.md` | Every significant choice, why it was made, and what would reopen it |
| `docs/KNOWN-ISSUES.md` | Accepted residuals, and defects found and fixed |
| `docs/ROADMAP.md` | Phase sequence and what Ordering will need |

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
dotnet ef database update -p src/AgenticShop.Catalog -s src/AgenticShop.Catalog
dotnet ef database update -p src/AgenticShop.Stock   -s src/AgenticShop.Stock

# 5. run (two terminals, or `dotnet watch` each)
dotnet run --project src/AgenticShop.Catalog
dotnet run --project src/AgenticShop.Stock
```

PostgreSQL is published on `127.0.0.1:5432` only. Binding it to `0.0.0.0` would expose
the bootstrap superuser to everything else on the local network.

The init script runs only when the data volume is empty. After changing it, recreate the
volume with `docker compose down -v` — this discards all local data.

| | Catalog | Stock |
|---|---|---|
| API docs (Scalar) | <http://localhost:5081/scalar/v1> | <http://localhost:5082/scalar/v1> |
| OpenAPI document | <http://localhost:5081/openapi/v1.json> | <http://localhost:5082/openapi/v1.json> |
| Health | <http://localhost:5081/health> | <http://localhost:5082/health> |
| Manual requests | `http/catalog.http` | `http/stock.http` |

The services are independent — either can run alone. Stock makes no outbound calls, so it
does not need Catalog to be up.

Migrations are applied automatically on startup in `Development`, so step 4 is only
needed the first time or after changing the model.

## Build and test

```powershell
dotnet build
dotnet test                                              # everything — 276 tests
dotnet test tests/AgenticShop.Catalog.UnitTests          # fast, no Docker needed
dotnet test tests/AgenticShop.Stock.UnitTests            # fast, no Docker needed
dotnet test tests/AgenticShop.Catalog.IntegrationTests   # needs Docker
dotnet test tests/AgenticShop.Stock.IntegrationTests     # needs Docker
```

Integration tests start one real PostgreSQL container per collection and reset the
tables between tests. A real database is used instead of mocking EF Core because
mocks pass while the actual query fails — they hide migrations, unique indexes,
query filters, CHECK constraints and SQL translation bugs.

Stock also has parallel concurrency tests: a 40-way burst against 10 units must never
oversell, and a 20-way race to confirm one reservation must ship it exactly once.
Sequential state-machine tests cannot prove this — see `docs/ARCHITECTURE.md` §6.3.

`TreatWarningsAsErrors` is on, so a warning fails the build.

## Conventions

- Minimal APIs with one endpoint-group file per resource; no controllers.
- **Every request DTO implements `IRequestContract` and every endpoint group registers
  `DataAnnotationValidationFilter`.** Minimal APIs do *not* validate body DTOs
  automatically — that is an MVC `[ApiController]` behaviour. Without the filter the
  attributes in `Contracts/` are decorative and an over-length field reaches PostgreSQL,
  which rejects the insert and surfaces as a `500` instead of a `400`. This was a real
  bug, caught by integration testing and now regression-tested.
  The filter recurses into nested objects *and* collections, reporting the full member
  path — `Lines[0].Quantity`, `Groups[1].Cells[0].Quantity`, `Rows[1][0].Quantity`.
  `Validator.TryValidateObject` does not cascade on its own, and a collection must be
  traversed by element rather than by property (reflecting over a `List<T>` yields `Count`
  and `Capacity`, not its contents), so without this a `CreateOrderRequest` would validate
  its top level and silently skip every line. Neither service has a nested contract yet, so
  the behaviour is proven by synthetic contracts in Catalog's
  `DataAnnotationValidationFilterTests` — that suite is the specification Ordering inherits.
  `RequestContractCoverageTests` fails the build if a `*Request` DTO forgets the marker.
- Entities own their invariants (`private set`, static `Create`, explicit methods) —
  these guard semantics. Length and format limits on the wire live in `Contracts/` as
  DataAnnotations — those guard the boundary. The overlap is intentional. Guards run
  before any mutation, so a rejected call leaves the entity untouched.
- Derived values are computed properties, never stored — Stock's
  `Available = QuantityOnHand - Reserved` — and marked `builder.Ignore(...)` so a second
  copy of the truth cannot drift.
- **Every entity carries an `xmin` concurrency token.** Without it two overlapping writes
  both succeed and the second silently discards the first. Declared as a `uint` shadow
  property marked `IsConcurrencyToken().ValueGeneratedOnAddOrUpdate()`, which Npgsql maps
  to PostgreSQL's system column — so it emits no DDL. (Npgsql 10 has no
  `UseXminAsConcurrencyToken()` extension; the convention does the mapping.) On `Product` a
  missed token loses a price edit; on `StockItem` it oversells.
- Multi-entity writes commit in a **single `SaveChangesAsync`**, so EF wraps them in one
  transaction. Stock's counter update and reservation insert can never disagree — and that
  is the property Phase 2's outbox will rely on.
- **Let the database reject duplicates rather than pre-checking.** `SELECT`-then-`INSERT` has
  a race window under retry; a unique index does not. Catalog's unique `sku` and Stock's
  `UNIQUE(order_id, stock_item_id)` are what make their create paths naturally idempotent.
- Errors are `ProblemDetails` with a `correlationId` extension, produced by
  `IExceptionHandler`, under four rules:
  - **A client error is never a 5xx.** `BadHttpRequestException` — malformed JSON, absent
    body, unconvertible value, non-numeric route or query parameter — is answered with the
    status the framework already chose, not a hardcoded `400`, because an oversized body
    is a `413`. 5xx rates and error logs are how operators decide whether to page someone.
  - **A server fault is never a 4xx.** A violated `CHECK` constraint restates an invariant
    the entity already guards, so it can only mean our code has a bug — that belongs in the
    5xx bucket, not the caller's error budget.
  - **Client cancellation is not an error.** `OperationCanceledException` with an aborted
    request is logged at `Debug` and handed back to the framework. An internal timeout
    without an aborted request still counts as a fault.
  - **No exception message is returned to the client.** Those messages carry internal
    parameter names and are formatted with the server's culture — on a Turkish-locale host
    `-5.00` renders as `-5,00`. They are logged, where the correlation id ties them to the
    response. Domain conflicts build their message from typed exception properties instead.
    Unique violations map to `409` keyed on the *constraint name*, so a second unique index
    cannot be misreported as a duplicate SKU.
  Severity is decided by the handler, not by EF Core — so
  `Microsoft.EntityFrameworkCore.Update` is silenced in `appsettings.json`, which would
  otherwise log a full Error-level stack trace for every routine 409.
- Required configuration is validated at startup. A missing `ConnectionStrings:<Service>`
  throws during host construction rather than producing a service that boots, reports
  healthy, and then 500s on every request. The value is read *lazily* inside the
  `AddDbContext` callback and validated *after* `Build()`: reading it eagerly would
  freeze it before a host applies its configuration overrides, which once caused the
  integration tests to run silently against a developer's own database.
- `X-Correlation-Id` is accepted on the way in and always echoed, so one request can
  be traced across all three services once orchestration lands. Because the value ends up
  in response headers, in the ProblemDetails body and in every log line, it is treated as
  untrusted: over 128 characters, or containing anything outside ASCII letters, digits and
  `-_.`, it is replaced with a freshly minted id rather than echoed. It is replaced rather
  than rejected, because a malformed correlation id does not make the request itself invalid.
  Propagation is inbound only — no service makes an outbound call yet.
- **Catalog-only, not house style:** soft delete (`IsActive` + `HasQueryFilter` +
  `includeInactive`), offset paging, and `numeric(18,2)` money with a `Currency` column.
  Stock has none of these — a silently filtered stock row would read as "no stock", which is
  more dangerous than a deleted product. Currency is validated as three ASCII letters per
  ISO 4217, not merely three characters; the registry of assigned codes is deliberately not
  embedded, because it changes and a stale allow-list would reject legitimate values.
- A contract's limits must match the columns they write to. `ContractSchemaAlignmentTests`
  reads them from the EF model and fails on drift — Catalog's compares string lengths
  against `varchar` limits (the drift that produced the original over-length-SKU 500),
  Stock's compares the enum vocabulary against its `CHECK` list and quantity bounds against
  `StockItem.MaxQuantity`. **Each service derives its own**; a copied guard would have
  matched nothing in Stock and passed without proving anything.
- Postgres identifiers are `snake_case` via `EFCore.NamingConventions`. This also renames
  EF's `__EFMigrationsHistory` columns to `migration_id` / `product_version`.

**Service boundaries are enforced at build time.** `Directory.Build.targets` fails the
build if any project under `src/` holds a `ProjectReference` to another project under
`src/`. Services communicate over HTTP only; a compile-time reference would make the
solution a distributed monolith in all but name and remove the failure modes the later
phases exist to teach. Ordering inherits the rule the moment it is created — there is no
test anyone has to remember to write. Test projects are exempt.

Note that `dotnet run` always applies `launchSettings.json`, whose profile pins
`ASPNETCORE_ENVIRONMENT=Development`; pass `--no-launch-profile` to override it from the
command line. Because startup runs `MigrateAsync()` in Development, the service fails fast
if PostgreSQL is not reachable — start the database first.

## Credentials

Every credential in this repository is a **published placeholder for a local, disposable
database** — not a secret, and never to be reused.

| Where | What | Why it is safe |
|---|---|---|
| `.env.example` | bootstrap superuser + three service-role passwords | copied to `.env`, which is gitignored; values are placeholders |
| `appsettings.Development.json` | `catalog_svc` and `stock_svc` connection strings | Development-only files; container is loopback-bound and holds disposable data |
| `CatalogApiFixture`, `StockApiFixture` | Testcontainers credentials | throwaway containers that exist only for the duration of a test run |

The guards that keep this true:

- `docker-compose.yml` publishes PostgreSQL on `127.0.0.1` only.
- Applications connect with least-privilege roles; the superuser exists only to bootstrap.
- No `appsettings.json` (non-Development) contains a connection string, and startup fails
  if one is absent — so a real environment must supply `ConnectionStrings__<Service>`
  through the environment or a secret store.
- Both `ComposeConfigurationTests` fail if a literal password appears in the compose file or
  if the port binding stops being loopback-only.

To use different values locally, edit `.env` and run
`dotnet user-secrets set "ConnectionStrings:Catalog" "..."` (or `:Stock`) against the
`UserSecretsId` already declared in each service project. No secret-management system is
introduced until there is a deployment to manage secrets for.

## Roadmap

Each phase adds one capability, and only once the previous phase makes it necessary.

**Phase 0 — Foundation** *(current: Catalog and Stock complete, Ordering outstanding)*
Three hosts, synchronous HTTP, one database per service, Testcontainers. Catalog and Stock
are implemented and verified; Ordering has its database and role provisioned but no code.

**Phase 1 — Reliability and observability**
Retry/circuit-breaker/timeout policies · idempotency keys · Serilog ·
OpenTelemetry distributed tracing · dependency-aware health checks ·
Dockerfiles and a `full` compose profile · CI. Retry is driven by a measured problem: a
40-way burst against 10 units of stock held only 4, because `xmin` conflicts are not retried.

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
