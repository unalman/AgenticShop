# AGENTS.md

**Global** operational rules for AI agents working in this repository. Read before changing code.

> **Working on one service? Also read its own `AGENTS.md`** — `src/AgenticShop.Catalog/`,
> `src/AgenticShop.Stock/`, `src/AgenticShop.Ordering/`. Service-specific rules live there; this
> file holds only what applies everywhere. **Applying one service's convention to another is the
> most likely mistake available here.**

Rationale, history, deferred items and known issues live in `docs/` — `ARCHITECTURE.md`,
`DECISIONS.md`, `KNOWN-ISSUES.md`, `ROADMAP.md` (phases), `TECHNOLOGY.md` (stack) — and in the
same three files under each `src/<Service>/docs/`. This file is the subset an agent needs to work
safely; where it summarises, `docs/` explains.

## 1. Project overview

AgenticShop is an educational .NET backend for learning agentic software development, being
evolved gradually into a distributed e-commerce backend. **The deferrals in §8 are deliberate** —
do not "improve" the project by adding infrastructure it has declined, and see `docs/ROADMAP.md`
for what each phase unblocks.

**Phase 0 complete, Phase 1 in progress.** All three services are implemented: **Catalog** is the
reference every convention originates from, **Stock** follows it with the deliberate divergences
recorded in §10, **Ordering** is the orchestrator — the only service with outbound calls, and the
one that forced the conventions to be re-derived rather than copied. **`AgenticShop.Shared`** was
extracted in Phase 1 and is not a service: it holds the filter, the correlation middleware,
`IRequestContract`, the logging, tracing and health-response configuration and the exception-handler
skeleton.

Verified baseline: `dotnet build` → 0 errors, 0 warnings. `dotnet test` → **503 pass**
(Shared 38 unit; Catalog 67 unit + 39 integration; Stock 81 unit + 61 integration; Ordering 140 unit
+ 77 integration). Database isolation verified 22/22.

## 2. Service boundaries

| Service | Port | Database | PostgreSQL role |
|---|---|---|---|
| Catalog | 5081 | `agenticshop_catalog` | `catalog_svc` |
| Stock | 5082 | `agenticshop_stock` | `stock_svc` |
| Ordering | 5083 | `agenticshop_ordering` | `ordering_svc` |

- One process, one database, one migration history per service. Independently deployable.
- **No cross-service `ProjectReference`.** `Directory.Build.targets` fails the build if a project
  under `src/` references another under `src/`. A consumer defines its own client contract — an
  interface plus a typed `HttpClient` — inside its own project.
- Cross-service references are `Guid` ids resolved over HTTP. Never foreign keys, joins or shared
  tables.
- **One exemption, by name: `AgenticShop.Shared`** — cross-cutting infrastructure only. It is *not*
  a service: no endpoints, no `DbContext`, no domain, no configuration. `Directory.Build.props`
  names it once and `Directory.Build.targets` exempts exactly that name, so a reference between two
  services is still a build error. The dependency points one way only — Shared may not reference
  any service. **Do not put a domain type, a contract DTO or anything service-specific in it.**
- Ports are fixed in each service's `Properties/launchSettings.json`. Use the ones above.

## 3. Database boundaries

One PostgreSQL 17 container, three databases, one per service.

- Each service connects with its own **non-superuser** role that owns its database. The
  `agenticshop` superuser bootstraps databases and roles only — **no application uses it**.
- A service must not access another service's database. PostgreSQL enforces this via per-role
  ownership plus `REVOKE ALL ON DATABASE ... FROM PUBLIC`.
- PostgreSQL is published on **`127.0.0.1` only**. Never bind `0.0.0.0`.
- `docker/postgres/init-dbs.sh` creates roles and databases and runs **only when the data volume
  is empty**. After changing it, `docker compose down -v` — which destroys all local data. State
  that before doing it.

Verify after any change to `init-dbs.sh` or `docker-compose.yml`:

```powershell
docker compose exec db bash /usr/local/bin/verify-db-isolation   # 22 checks; non-zero on failure
```

## 4. Technology

.NET 10 (`net10.0`) · ASP.NET Core **Minimal APIs**, no controllers · PostgreSQL 17 via EF Core ·
xunit + FluentAssertions with Testcontainers · `.slnx`. The full stack, its versions and where
each is pinned: `docs/TECHNOLOGY.md`.

- **`TreatWarningsAsErrors` is on.** A warning fails the build. Fix it; do not suppress it.
- All package versions live in `Directory.Packages.props`; a `PackageReference` must omit
  `Version`. `CentralPackageTransitivePinningEnabled` is on for a reason — `docs/DECISIONS.md`.
- `dotnet-tools.json` pins `dotnet-ef`. Run `dotnet tool restore`.
- `.gitattributes` forces **LF** on `*.sh`; a CRLF script fails inside the container.
- `dotnet` CLI output is **Turkish-localised**: filter on `Hata`, `Uyarı`, `Başarılı`, `Başarısız`.
- **Do not bump the TFM to `net11.0`** — .NET 11 is pre-release and not installed here.

## 5. Coding conventions

**`docs/ARCHITECTURE.md` §4 is the full text of every convention, with the evidence and the
reasoning; §5.5 inventories the structural guards. Read the matching §4.x before implementing a
convention from scratch or changing one, and read the reference implementation — Catalog — before
writing code in any service.** What follows is deliberately *not* a summary of §4. It is only what
neither the reference nor a guard will tell you: the rules that fail silently, or that you would
otherwise guess wrong.

- **Layers (§4.1).** No repository, service or mediator layer — handlers call the `DbContext`
  directly. `Middleware/` and `Validation/` no longer exist in a service; they live in
  `AgenticShop.Shared`. The one sanctioned exception is Ordering's `OrderPlacer`, in `Endpoints/`
  beside its single caller: no interface, no second consumer, not the start of a layer.
- **Entities (§4.2).** Guards run before any mutation, so a rejected call leaves the entity
  byte-identical. Money is `decimal` plus a separate `Currency` string, rounded 2dp `AwayFromZero`
  — no `Money` value object, and **Stock has no money at all**. A domain rejection that must map to
  409 gets its own exception type carrying typed properties; the handler builds the client message
  from those, never from `Message`. `ArgumentException` cannot express a 409.
- **EF Core (§4.3).** Optimistic concurrency is mandatory on every entity —
  `builder.Property<uint>("xmin").IsConcurrencyToken().ValueGeneratedOnAddOrUpdate();` which emits
  no DDL, because `UseXminAsConcurrencyToken()` **does not exist in Npgsql 10**. Never
  `HasDefaultValue` on a non-nullable `bool`: an explicit `false` is dropped from the INSERT and
  comes back as the default. `CHECK` SQL is verbatim, so it names columns *after* snake_case
  renaming, and reading constraints back needs `db.GetService<IDesignTimeModel>().Model`. Name
  unique indexes through a `public const` — the handler branches on the name. A foreign key inside
  one service's own database is fine; only a key crossing a service boundary is forbidden.
- **Migrations (§4.4).** `MigrateAsync()` runs at startup **only in `Development`** — do not remove
  that guard.
- **Endpoints (§4.5).** **Every group registers
  `.AddEndpointFilter<DataAnnotationValidationFilter>()`.** `/openapi/v1.json` and `/scalar/v1` are
  **Development-only**. Multi-entity writes commit in one `SaveChangesAsync`, so EF wraps them in
  one transaction — Phase 2's outbox will depend on it, and needing one across several saves is a
  design smell to raise first. Let the database reject duplicates; do not pre-check.
- **Validation (§4.6).** An attribute on a positional record parameter takes the `[property: ...]`
  target, or it can bind to the parameter and the validator never sees it. **Minimal APIs do not
  validate body DTOs on their own** — without the filter the attributes are decorative and bad
  input reaches PostgreSQL. The filter recurses into nested objects and collections **by element**,
  reporting paths like `Lines[0].Quantity`.
- **Errors (§4.7).** A service supplies only `ClassifyDomain` (returning `null` to fall through),
  `UniqueViolationDetail` and `ConcurrencyDetail`; the base owns the rest. `BadHttpRequestException`
  answers with its **own `StatusCode`, never a hardcoded 400** — an oversized body is 413. Arm order
  is load-bearing: domain arms precede both EF arms, and `DbUpdateConcurrencyException` precedes
  `DbUpdateException`, which it derives from. `23505` → 409 keyed on the **constraint name**,
  `22001`/`22003` → 400, `ArgumentException` → 400, a failed dependency → **502** (Ordering only),
  anything else → 500. **Do not add an arm for `23514`** — a CHECK restates an invariant the entity
  already guards, so a violation means *our* code has a bug and belongs in the 5xx bucket. **No
  exception message reaches the client:** `detail` is `null` for domain 400s and all 500s, built
  from typed properties for domain 409s. **The handler decides severity:** a 4xx logs one `Warning`
  with no exception object, a 5xx logs `Error` *with* it.
- **Correlation ID (§4.8).** The id **is** the W3C trace id — `Activity.Current.TraceId`, which
  ASP.NET Core populates for every request whether or not OpenTelemetry is registered. Do not mint a
  parallel one. `X-Correlation-Id` is **response-only**: an inbound value is ignored, so there is
  nothing to sanitise and no charset guard to maintain. Propagation across a hop is `traceparent`,
  done by the instrumentation — **do not add a `DelegatingHandler` for it**. Use
  `CorrelationIdMiddleware.HeaderName`, never the literal string. Registered first, before
  `UseExceptionHandler`.

## 6. Testing rules

Two projects per service: `<Service>.UnitTests` and `<Service>.IntegrationTests`. Section
references below are to `docs/ARCHITECTURE.md`.

| | Unit | Integration |
|---|---|---|
| Scope | domain rules, validation filter, error classification | real API over HTTP against real PostgreSQL |
| I/O | none | one Testcontainers container per collection |
| Docker | not needed | **required** |

- **Never mock EF Core** (§5.1). Only real PostgreSQL surfaces a `23505` violation, a `varchar(64)`
  rejection, the `xmin` system column, or query-filter SQL translation.
- Unit-testing ASP.NET Core types needs
  `<FrameworkReference Include="Microsoft.AspNetCore.App" />` in the unit test project.
- One container per collection: `ICollectionFixture<<Service>ApiFixture>` +
  `[Collection("<Service> API")]`. xunit runs a collection sequentially, which is what makes
  per-test `ResetDatabaseAsync()` safe. Dispose the container in a `finally` so a throw from the
  factory cannot leak it.
- `Program.cs` must end with `public partial class Program;` — the `WebApplicationFactory` anchor.
- **Resolve the connection string lazily inside the `AddDbContext` callback** (§5.4). Reading it
  into a local first freezes it before `WebApplicationFactory` applies its override, and the tests
  silently run against the developer's own database.
- **Assert failure paths as rigorously as success paths.** The missing 400/404/409 assertions are
  exactly what shipped as 500s.
- **Anything with a mutable counter needs a parallel test** (§5.3) — a sequential "confirm twice →
  409" test still passes with the concurrency token deleted.
- **Parallel assertions are inequalities, never exact counts** (§5.3). Assert what must always hold
  — never oversold, no 5xx, counters agree with committed rows, every request answered — and cover
  the exact boundary in a separate *sequential* test.
- **Structural conventions get automated guards** — a convention enforced only by prose will be
  violated. The inventory is §5.5. **These guards are duplicated per service on purpose**: the
  assemblies must not reference each other. Copy them; do not try to share them.
- **Build and run the tests before declaring a task complete.** Report actual numbers, not
  "tests pass". Also run the integration tests once with `docker compose stop db` to prove the
  harness is isolated.
- Clean up any rows a smoke or E2E check creates.

Further harness gotchas: §5.4. Concurrency-testing rules and measurements: §5.3.

## 7. Configuration and security

- **No production secrets in source control.** No non-Development `appsettings.json` contains a
  connection string, and startup **fails** if one is absent — a real environment must supply
  `ConnectionStrings__<Service>` via environment or secret store.
- **`.env` is never committed** (gitignored, `.gitignore:7`). `.env.example` is committed and
  documents every variable.
- Every credential in the repo is a **published local-only placeholder** (`local_only_bootstrap`,
  `local_only_catalog`, `local_only_stock`, `local_only_ordering`) and must be labelled as such
  wherever it appears.
- **No literal credential in `docker-compose.yml`** — all use `${VAR:?message}`, so a missing
  value fails loudly. `ComposeConfigurationTests` enforces this.
- **Validate required configuration at startup**, not on first use.
- **Declare the test environment explicitly** (`builder.UseEnvironment(...)`); never rely on a
  framework default.
- Do not introduce a secret-management system; there is no deployment yet.

`dotnet run` **always** applies `launchSettings.json`, whose profile pins
`ASPNETCORE_ENVIRONMENT=Development` and overrides the command line — pass `--no-launch-profile`
to select another environment. Because `Development` triggers `MigrateAsync()`, the service
cannot start without a reachable database.

## 8. Architectural constraints — do NOT introduce

Deferred by design, not oversights. Phase assignments: `docs/ROADMAP.md`. The reasoning behind
each deferral, including the few with no assigned phase: `docs/DECISIONS.md`.

**Still deferred — do not add:**

**RabbitMQ or any broker · Outbox / Inbox · Redis · authentication / authorization ·
Kubernetes / Helm · API gateway (YARP) · CQRS · DDD tactical patterns · event sourcing · service
Dockerfiles · CI pipeline.**

Also do not add at this size: repository or service layers, an `Application` layer, a mediator,
`Result<T>` monads, a mapping framework, domain-event plumbing, or a `Money` value object.

The ordering matters: retrying without idempotency keys double-reserves stock; an outbox without
a broker is dead weight; CQRS without read pressure is ceremony.

**Now permitted, because Phase 1 has started — but only the ones that are done:**
the shared infrastructure library (`AgenticShop.Shared`, §2) · **idempotency keys on `POST /orders`**
(Ordering decision O17) · **resilience on Ordering's typed clients** (Ordering decision O18) ·
**Serilog in all three services** (`docs/DECISIONS.md` → "Serilog, adopted in Catalog first",
configured once in `AgenticShop.Shared/Logging`) · **OpenTelemetry tracing in all three** (→
"OpenTelemetry, and the correlation id became the trace id", in `AgenticShop.Shared/Tracing`) ·
**dependency-aware health checks** (`docs/ARCHITECTURE.md` §4.10, response writer in
`AgenticShop.Shared/Health`). Nothing else on the list above has been unblocked.

Two of those carry a rule that is easy to get wrong, and only one of them is fully test-guarded.
**Do not extend retry to another outbound call before reading O18** — the exemption on reserve is what
stops a retry from stranding a hold and reporting it as out-of-stock. **A health check that calls
another service must carry the `ready` tag**, or it lands on `/health` and a liveness endpoint that
depends on a downstream turns one outage into two; the existing test pins Catalog and Stock by name,
so it will *not* catch a third check added without the tag.

## 9. Git rules

- **Do not commit unless explicitly asked.** Finishing a task is not permission to commit.
- **Do not modify unrelated files.** If a fix needs something outside its stated scope, stop and
  raise it rather than folding it in.
- **Inspect `git diff` and `git status` before finishing.** Confirm the change set matches the
  request, no build artefacts are staged, and `.env` is absent.
- Stage only paths belonging to the change. Never `git add -A` with unrelated changes present.
- Do not push, force-push, rebase, amend or reset without being asked.
- Default branch is `main`. `bin/`, `obj/` and `.env` are gitignored.
- For destructive actions — `docker compose down -v`, deleting files, recreating a volume — state
  the blast radius and confirm nothing of value is lost first.

## 10. Reference implementation rules

**Catalog is the structural reference; Stock is the worked example of adapting it; Ordering is the
test of whether the conventions survive a service that orchestrates.** Before copying a convention
into another service, verify it still fits.

**Must change per service:** the connection-string key, all `<Service>*` type names, the
constraint names in `UniqueViolationDetail`, conflict-message wording, the entities under test in
`ContractSchemaAlignmentTests`, and the port / database / role.

**Needs judgement, not copying:** soft delete, paging, money, domain guards, and which structural
guards are meaningful for the entities at hand. A copied guard that matches nothing passes
vacuously.

**Copy verbatim, namespace only — the test harness alone:** `<Service>ApiFactory` / `Fixture` /
`Collection`, `TestPostgreSql`, `TestHostIsolationTests`, `ComposeConfigurationTests`. These stay
per service because the three integration assemblies must not reference each other, and for the
same reason **the per-service structural guards are never shared, in any phase**. Everything else
that used to be copied is now **referenced, not copied** — `DataAnnotationValidationFilter`,
`CorrelationIdMiddleware`, `IRequestContract` and the `ProblemDetailsExceptionHandler` base all
live in `AgenticShop.Shared`. See `docs/DECISIONS.md` → "Shared infrastructure library".

**Do not re-test shared infrastructure.** The filter and the middleware are specified once, in
`AgenticShop.Shared.UnitTests`; each service asserts only that the filter is *wired* to its own
endpoint groups, plus — for Ordering, the one service with a real nested DTO — that the cascade
works over HTTP. Handler tests stay per service: each exercises that service's own arms and
messages.

**A shared type's guards must not be anchored on a shared type.** `RequestContractCoverageTests`
and `ContractSchemaAlignmentTests` reflect over an assembly to find a service's DTOs; anchoring on
`IRequestContract` scans `AgenticShop.Shared`, which holds none, so the guard passes having matched
nothing. Each anchors on one of its own contract types, and
`TheCoverageCheckIsNotPassingVacuously` catches a regression.

**When a convention proves wrong for a later service, fix it in the earlier ones too.** The open
divergences are listed in `docs/KNOWN-ISSUES.md`.

Each service's divergences are tabulated with reasons in its own `docs/ARCHITECTURE.md` — Stock §8,
Ordering §10. Those tables are the count and the authority; **do not restate a divergence list or a
number here.** None of them is a convention the other services should adopt.
