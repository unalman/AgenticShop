# AGENTS.md

**Global** operational rules for AI agents working in this repository. Read before changing code.

> **Working on one service? Also read its own `AGENTS.md`.**
> `src/AgenticShop.Catalog/AGENTS.md` · `src/AgenticShop.Stock/AGENTS.md` ·
> `src/AgenticShop.Ordering/AGENTS.md`
>
> Service-specific rules live there — soft delete, paging and money for Catalog; the leaf-service
> rule, reservation state machine and counter invariant for Stock; the write-once order, the
> confirm-phase policy and the downstream client seam for Ordering. This file holds only what
> applies everywhere. Applying one service's convention to another is the most likely mistake
> available here.

Rationale, history, deferred items and known issues live in `docs/` and in each service's own
`docs/`:

| Scope | Architecture | Decisions | Known issues |
|---|---|---|---|
| **Global** | `docs/ARCHITECTURE.md` | `docs/DECISIONS.md` | `docs/KNOWN-ISSUES.md` |
| **Catalog** | `src/AgenticShop.Catalog/docs/ARCHITECTURE.md` | `…/DECISIONS.md` | `…/KNOWN-ISSUES.md` |
| **Stock** | `src/AgenticShop.Stock/docs/ARCHITECTURE.md` | `…/DECISIONS.md` | `…/KNOWN-ISSUES.md` |
| **Ordering** | `src/AgenticShop.Ordering/docs/ARCHITECTURE.md` | `…/DECISIONS.md` | `…/KNOWN-ISSUES.md` |

Phases are in `docs/ROADMAP.md`. This file is the subset an agent needs to work safely; where it
summarises, `docs/` explains.

## 1. Project overview

AgenticShop is an educational .NET backend for learning agentic software development, being
evolved gradually into a distributed e-commerce backend. The deferrals in §8 are deliberate —
do not "improve" the project by adding infrastructure it has declined.

**Phase 0 complete, Phase 1 in progress.** All three services exist; the first Phase 1 item — the
shared infrastructure library — is done.

| Service | Status |
|---|---|
| **Catalog** | **Implemented.** The reference implementation; every convention originates here. |
| **Stock** | **Implemented.** Follows Catalog's conventions, with the deliberate divergences recorded in §10. |
| **Ordering** | **Implemented.** The orchestrator: the only service with outbound calls, and the one that forced the conventions to be re-derived rather than copied. |
| **Shared** | **Extracted in Phase 1.** Not a service — the filter, correlation middleware, `IRequestContract` and the exception-handler skeleton. |

Everything still deferred is deferred on purpose — see §8 and `docs/ROADMAP.md`.

Verified baseline: `dotnet build` → 0 errors, 0 warnings. `dotnet test` → **471 pass**
(Shared 35 unit; Catalog 64 unit + 39 integration; Stock 77 unit + 61 integration; Ordering 120 unit
+ 75 integration). Database isolation verified 22/22.

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
- **One exemption, by name: `AgenticShop.Shared`.** Extracted in Phase 1, it holds cross-cutting
  infrastructure only — the validation filter, the correlation middleware, `IRequestContract` and
  the `ProblemDetailsExceptionHandler` skeleton. It is *not* a service: no endpoints, no
  `DbContext`, no domain, no configuration. `Directory.Build.props` names it once and
  `Directory.Build.targets` exempts exactly that name, so a reference between two services is
  still a build error. The dependency only ever points one way — the shared project may not
  reference any service. **Do not put a domain type, a contract DTO or anything
  service-specific in it.**
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

| Concern | Choice | Version |
|---|---|---|
| SDK | pinned in `global.json`, `rollForward: latestFeature` | 10.0.400 |
| TFM | set once in `Directory.Build.props` | `net10.0` |
| Web | ASP.NET Core **Minimal APIs** — no controllers | 10.0.12 |
| Database | PostgreSQL, image `postgres:17-alpine` | 17 |
| ORM / provider | EF Core / `Npgsql.EntityFrameworkCore.PostgreSQL` | 10.0.12 / 10.0.3 |
| Naming | `EFCore.NamingConventions` → snake_case | 10.0.1 |
| API docs | `Microsoft.AspNetCore.OpenApi` + `Scalar.AspNetCore` | 10.0.12 / 2.17.8 |
| Containers | Docker / Compose | 29.8.0 / 5.5.1 |
| Test DB / host | `Testcontainers.PostgreSql` / `Mvc.Testing` | 4.15.0 / 10.0.12 |
| Tests | xunit + FluentAssertions | 2.9.3 / 8.11.0 |
| Solution | `.slnx` | — |

- **`TreatWarningsAsErrors` is on.** A warning fails the build. Fix it; do not suppress it.
- All package versions live in `Directory.Packages.props`; a `PackageReference` must omit
  `Version`. `CentralPackageTransitivePinningEnabled` is on for a reason — `docs/DECISIONS.md`.
- `dotnet-tools.json` pins `dotnet-ef`. Run `dotnet tool restore`.
- `.gitattributes` forces **LF** on `*.sh`; a CRLF script fails inside the container.
- `dotnet` CLI output is **Turkish-localised**: filter on `Hata`, `Uyarı`, `Başarılı`, `Başarısız`.
- **Do not bump the TFM to `net11.0`** — .NET 11 is pre-release and not installed here.

## 5. Coding conventions

**These are the rules. `docs/ARCHITECTURE.md` §4 is the canonical explanation of each, with the
evidence and the reasoning — read it before changing a convention, not before following one.**
Service-specific design: `src/<Service>/docs/ARCHITECTURE.md`.

**Structure.** One project per service. **No** `Api`/`Core`/`Application` split, no repository,
service or mediator layer — handlers call the `DbContext` directly.

```
Clients/     downstream contracts: an interface + typed HttpClient per service (orchestrators only)
Contracts/   request+response records, implementing the shared IRequestContract marker
Data/        DbContext, IEntityTypeConfiguration<T>, Migrations/
Domain/      entities and invariants — must not reference EF Core or ASP.NET Core
Endpoints/   one static class per resource
Errors/      the service's ProblemDetailsExceptionHandler subclass — domain arms and messages only
```

`Clients/` exists only in Ordering, and `Domain/` must never reference it. **`Middleware/` and
`Validation/` no longer exist in a service** — the correlation middleware and the validation filter
live in `AgenticShop.Shared`, as does `IRequestContract`. The single sanctioned exception to
"handlers call the `DbContext` directly" is Ordering's `OrderPlacer`, in `Endpoints/` beside its
one caller — pre-authorised by `docs/DECISIONS.md`, no interface, no second consumer, and not the
start of a layer.

**Entities.** `private set` everywhere; `private` parameterless constructor for EF; `static
Create(...)` factory; explicit mutation methods, not public setters. **Guards run before any
mutation.** Length limits are `public const int`, shared by `Contracts/` and `Data/`.
`DateTimeOffset` for all timestamps. Identity fields other services hold are immutable.

- **Derived values are computed properties, never stored** — mark them `builder.Ignore(...)`.
  Stock's `Available = QuantityOnHand - Reserved` is the template.
- Money is `decimal` plus a separate `Currency` string — no `Money` value object — rounded 2dp
  `AwayFromZero`. **Stock has no money at all**; do not add fields a service does not need.
- **Domain rejections get their own exception type carrying typed properties**, e.g.
  `InsufficientStockException(Available, Requested)`. The handler builds the client message from
  those properties, never from `Message` (logs only). `ArgumentException` cannot express a 409.

**EF Core.** One `IEntityTypeConfiguration<T>` per entity via `ApplyConfigurationsFromAssembly`.
Explicit snake_case `ToTable`. `HasPrecision(18, 2)` on money. `UseSnakeCaseNamingConvention()` on
the context. Name unique indexes through a `public const` — the exception handler branches on it.

- **Optimistic concurrency is mandatory on every entity:**
  `builder.Property<uint>("xmin").IsConcurrencyToken().ValueGeneratedOnAddOrUpdate();`
  `UseXminAsConcurrencyToken()` **does not exist in Npgsql 10**. This emits no DDL.
- **Never `HasDefaultValue` on a non-nullable `bool`** — EF's sentinel is `false`, so an explicit
  `false` is dropped from the INSERT and silently comes back as the default.
- **A foreign key is fine within one service's database.** Only a key crossing a service boundary
  is forbidden.
- **`CHECK` constraints:** their SQL is verbatim, so it must name columns *after* snake_case
  renaming. Reading them back needs `db.GetService<IDesignTimeModel>().Model`;
  `GetCheckConstraints()` throws against the runtime model.
- Enums map with `.HasConversion<string>()` plus a `CHECK` listing the names.

**Migrations.** Per service under `Data/Migrations/`. Set `ASPNETCORE_ENVIRONMENT=Development`
first so `appsettings.Development.json` supplies the connection string:

```powershell
dotnet ef migrations add <Name> -p src/AgenticShop.Catalog -s src/AgenticShop.Catalog
dotnet ef migrations script  ...   # inspect SQL before applying anything non-trivial
```

`MigrateAsync()` runs at startup **only in `Development`**. Do not remove that guard.

**Endpoints.** One `static class <Resource>Endpoints` with a
`Map<Resource>Endpoints(this IEndpointRouteBuilder)` extension called from `Program.cs`.
`MapGroup("/api/v1/<resource>")` + `.WithTags(...)`, and **every group registers
`.AddEndpointFilter<DataAnnotationValidationFilter>()`**. Handlers are `private static async
Task<IResult>`. Declare `.WithName`, `.WithSummary` and `.Produces*` on every route.
`.AsNoTracking()` on reads. `201`+`Location` on create, `200`+body on update, `204` on delete,
`404` when missing, `409` on conflict. `/health` is always mapped; `/openapi/v1.json` and
`/scalar/v1` are **Development-only**.

**Validation.** Boundary limits are DataAnnotations on `Contracts/` records (`[property: ...]` on
positional parameters); domain guards protect invariants. Both, deliberately.

- **Every request DTO implements `IRequestContract`** — the filter only validates marked types.
- **Minimal APIs do not validate body DTOs automatically.** Without the filter the attributes are
  decorative and bad input reaches PostgreSQL.
- The filter recurses into nested objects *and* collections, reporting full paths
  (`Lines[0].Quantity`). Collections are traversed **by element**, not by property.
- Failures return `Results.ValidationProblem(...)`.

**Errors.** Each service has a `<Service>ExceptionHandler` deriving from the shared
`ProblemDetailsExceptionHandler`, which owns `TryHandleAsync`, the cancellation arm, the severity
decision, the logging and the ProblemDetails shape. **A service supplies only three things:** its
domain arms (`ClassifyDomain`, returning `null` to fall through), its unique-violation messages
(`UniqueViolationDetail`) and its concurrency wording (`ConcurrencyDetail`). Classification, in
switch order:

| Exception | Status |
|---|---|
| `OperationCanceledException` **and** `RequestAborted` | not handled — `LogDebug`, `return false` |
| `BadHttpRequestException` | **`badRequest.StatusCode`** — never hardcode 400; an oversized body is 413 |
| domain conflict exceptions | 409 — built from typed properties. Place before the EF arms |
| a dependency failed | **502** — Ordering only |
| `DbUpdateConcurrencyException` | 409 (must precede `DbUpdateException`, which it derives from) |
| `DbUpdateException` + SQLSTATE `23505` | 409, message keyed on the **constraint name** |
| `DbUpdateException` + SQLSTATE `22001` / `22003` | 400 |
| `ArgumentException` | 400 |
| anything else | 500 |

**Do not add an arm for a CHECK-constraint violation (`23514`).** Every CHECK restates an invariant
the entity already guards, so a violation means *our* code has a bug and belongs in the 5xx bucket.

Four invariants, all test-guarded — the reasoning for each is in `docs/ARCHITECTURE.md` §4.7:
**(1)** a client error is never a 5xx; **(2)** a server fault is never a 4xx; **(3)** no exception
message reaches the client, so `detail` is `null` for domain 400s and all 500s and built from typed
properties for domain 409s; **(4)** the handler decides severity, not EF Core — handled rejections
log one `Warning` line with no exception object, unhandled failures log `Error` *with* it. Non-5xx
lines log the whole exception chain (`DescribeForLog`, depth cap 5).
`Microsoft.EntityFrameworkCore.Update` is `"None"` in `appsettings.json`.

**Correlation ID.** Use the constant `CorrelationIdMiddleware.HeaderName`; never hardcode the
string. Adopt an inbound value or mint `Guid.NewGuid().ToString("N")`. **Inbound values are
untrusted:** accept only 1–128 characters of ASCII letters, digits and `-_.`; otherwise
**replace** with a minted id, never reject the request. Registered first, before
`UseExceptionHandler`.

**Service-specific conventions are not listed here.** Soft delete, query filters, offset paging,
money and currency live in `src/AgenticShop.Catalog/AGENTS.md`; the leaf-service rule, the
reservation state machine, the counter invariant and the strict-transition rule live in
`src/AgenticShop.Stock/AGENTS.md`; the write-once order and the confirm-phase policy live in
`src/AgenticShop.Ordering/AGENTS.md`. **Read the service file before editing that service** — the
most dangerous mistake available is applying one service's convention to another.

**Multi-entity writes commit in one `SaveChangesAsync`** so EF wraps them in one transaction. This
is what Phase 2's outbox will depend on. Needing a transaction across several saves is a design
smell; raise it first.

**Let the database reject duplicates; do not pre-check.** `SELECT`-then-`INSERT` has a race window
under retry; a unique index does not.

## 6. Testing rules

Two projects per service: `<Service>.UnitTests` and `<Service>.IntegrationTests`.

| | Unit | Integration |
|---|---|---|
| Scope | domain rules, validation filter, error classification | real API over HTTP against real PostgreSQL |
| I/O | none | one Testcontainers container per collection |
| Docker | not needed | **required** |

- **Never mock EF Core.** Only real PostgreSQL surfaces a `23505` violation, a `varchar(64)`
  rejection, the `xmin` system column, or query-filter SQL translation.
- Unit-testing ASP.NET Core types needs
  `<FrameworkReference Include="Microsoft.AspNetCore.App" />` in the unit test project.
- One container per collection: `ICollectionFixture<<Service>ApiFixture>` +
  `[Collection("<Service> API")]`. xunit runs a collection sequentially, which is what makes
  per-test `ResetDatabaseAsync()` safe. Dispose the container in a `finally` so a throw from the
  factory cannot leak it.
- `Program.cs` must end with `public partial class Program;` — the `WebApplicationFactory` anchor.
- **Resolve the connection string lazily inside the `AddDbContext` callback.** Reading it into a
  local first freezes it before `WebApplicationFactory` applies its override, and the tests
  silently run against the developer's own database.
- **Assert failure paths as rigorously as success paths.** The missing 400/404/409 assertions are
  exactly what shipped as 500s.
- **Anything with a mutable counter needs a parallel test.** Sequential state-machine tests prove
  almost nothing about concurrency — the measurements are in `docs/ARCHITECTURE.md` §5.3, and a
  sequential "confirm twice → 409" test still passes with the concurrency token deleted.
- **Parallel assertions are inequalities, never exact counts.** Winners depend on interleaving and
  Phase 0 does not retry server-side. Assert what must always hold — never oversold, no 5xx,
  counters agree with committed rows, every request answered — and cover the exact boundary in a
  separate *sequential* test.
- **Structural conventions get automated guards** — a convention enforced only by prose will be
  violated. The inventory is `docs/ARCHITECTURE.md` §5.5; it includes `Directory.Build.targets`,
  `scripts/verify-db-isolation.sh`, and a boundary guard per service that asserts whether it may
  register an `HttpClient`.
- **These guards are duplicated per service on purpose** — the assemblies must not reference each
  other, and each needs its own drift check. Copy them; do not try to share them.
- **Build and run the tests before declaring a task complete.** Report actual numbers, not
  "tests pass". Also run the integration tests once with `docker compose stop db` to prove the
  harness is isolated — a suite that quietly uses the developer's database still passes.
- Clean up any rows a smoke or E2E check creates.

Further harness gotchas: `docs/ARCHITECTURE.md` §5.4. Concurrency-testing rules: §5.3.

## 7. Configuration and security

- **No production secrets in source control.** No non-Development `appsettings.json` contains a
  connection string, and startup **fails** if one is absent — a real environment must supply
  `ConnectionStrings__<Service>` via environment or secret store.
- **`.env` is never committed** (gitignored, `.gitignore:7`). `.env.example` is committed and
  documents every variable.
- Every credential in the repo is a **published local-only placeholder** (`local_only_bootstrap`,
  `local_only_catalog`, `local_only_stock`, `local_only_ordering`) and must be labelled as such
  wherever it appears. Safe because the container is loopback-bound, apps use least-privilege
  roles and the data is disposable.
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

**RabbitMQ or any broker · Outbox / Inbox · Redis · Polly or any resilience library ·
OpenTelemetry · Serilog or shared logging infrastructure · authentication / authorization ·
Kubernetes / Helm · API gateway (YARP) · CQRS · DDD tactical patterns · event sourcing ·
service Dockerfiles · CI pipeline.**

Also do not add at this size: repository or service layers, an `Application` layer, a mediator,
`Result<T>` monads, a mapping framework, domain-event plumbing, or a `Money` value object.

The ordering matters: retrying without idempotency keys double-reserves stock; an outbox without
a broker is dead weight; CQRS without read pressure is ceremony.

**Now permitted, because Phase 1 has started — but only the ones that are done:**
the shared infrastructure library (`AgenticShop.Shared`, §2) and **idempotency keys on
`POST /orders`** (Ordering decision O17 — a required `Idempotency-Key` header, a claim committed
before the first side effect, a completion written in the same transaction as the order). Nothing
else on the list above has been unblocked. Serilog, OpenTelemetry and dependency-aware health checks
are still ahead. **Resilience policies are now unblocked but not built** — the key existed to make
them safe, so do not add retry before reading O17.

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
vacuously — that already happened once.

**Copy verbatim, namespace only — the test harness alone.** `<Service>ApiFactory` / `Fixture` /
`Collection`, `TestPostgreSql`, `TestHostIsolationTests`, `ComposeConfigurationTests`. These stay
per service because the three integration assemblies must not reference each other.

Everything else that used to be copied is now **referenced, not copied**: `DataAnnotationValidationFilter`,
`CorrelationIdMiddleware` and `IRequestContract` live in `AgenticShop.Shared`, and the
exception-handler skeleton is the `ProblemDetailsExceptionHandler` base class. A service's handler
supplies only its domain arms and its two message strings.

**Do not re-test shared infrastructure.** The filter and the middleware are specified once, in
`AgenticShop.Shared.UnitTests`. Each service asserts only that the filter is *wired* to its own
endpoint groups, plus — for Ordering, the one service with a real nested DTO — that the cascade
works over HTTP. Re-testing shared code creates a second place to update and proves nothing.
Handler tests stay per service, because each exercises that service's own arms and messages.

**Duplication counter: extracted.** The 3-of-3 copies were consolidated into `AgenticShop.Shared`
as the first Phase 1 item. Reasoning and the two divergences that had to be reconciled:
`docs/DECISIONS.md` → "Shared infrastructure library". **The per-service structural guards are
still never shared, in any phase** — sharing them would require the test assemblies to reference
each other.

**A shared type's guards must not be anchored on a shared type.** `RequestContractCoverageTests`
and `ContractSchemaAlignmentTests` discover a service's DTOs by reflecting over an assembly. Now
that `IRequestContract` lives in `AgenticShop.Shared`, anchoring on it scans the shared project —
which holds no DTOs — and the guard passes having matched nothing. Each anchors on one of its own
contract types instead. This is the "copied guard that matches nothing" failure arriving through a
new door; the `TheCoverageCheckIsNotPassingVacuously` assertion is what catches it.

**When a convention proves wrong for a later service, fix it in the earlier ones too.** The open
divergences are listed in `docs/KNOWN-ISSUES.md`.

Each service's divergences are tabulated with reasons in its own `docs/ARCHITECTURE.md` — Stock §8,
Ordering §10. Those tables are the count and the authority; **do not restate a divergence list or a
number here.** None of them is a convention the other services should adopt, and none is one they
should have been following.
