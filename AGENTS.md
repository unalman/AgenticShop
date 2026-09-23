# AGENTS.md

Operational rules for AI agents working in this repository. Read before changing code.

Rationale, history, deferred items and known issues live in `docs/`: `ARCHITECTURE.md` (design
and the full Catalog reference) · `DECISIONS.md` (why each choice was made) ·
`KNOWN-ISSUES.md` (open and resolved) · `ROADMAP.md` (phases). This file is the subset an agent
needs to work safely; where it summarises, `docs/` explains.

## 1. Project overview

AgenticShop is an educational .NET backend for learning agentic software development, being
evolved gradually into a distributed e-commerce backend. The deferrals in §8 are deliberate —
do not "improve" the project by adding infrastructure it has declined.

**Phase 0 — exactly one service exists:**

| Service | Status |
|---|---|
| **Catalog** | **Implemented.** The reference implementation. Everything under `src/` and `tests/`. |
| Stock | **Does not exist.** Database and role provisioned; no project, no code. |
| Ordering | **Does not exist.** Database and role provisioned; no project, no code. |

Do not assume Stock or Ordering exist, and do not create them unless asked.

Verified baseline: `dotnet build` → 0 errors, 0 warnings. `dotnet test` → **138 pass**
(99 unit, 39 integration).

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
- **No shared library.** There is deliberately no `AgenticShop.Shared`, `Common` or `Contracts`
  project.
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

Full detail and rationale: `docs/ARCHITECTURE.md` §4.

**Structure.** One project per service. **No** `Api`/`Core`/`Application` split, no repository,
service or mediator layer — handlers call the `DbContext` directly.

```
Contracts/   request+response records, IRequestContract marker
Data/        DbContext, IEntityTypeConfiguration<T>, Migrations/
Domain/      entities and invariants — must not reference EF Core or ASP.NET Core
Endpoints/   one static class per resource
Errors/      IExceptionHandler
Middleware/  correlation id
Validation/  DataAnnotationValidationFilter
```

**Entities.** `private set` everywhere; `private` parameterless constructor for EF; `static
Create(...)` factory; explicit mutation methods, not public setters. **Guards run before any
mutation**, so a rejected call leaves the entity untouched. Length limits are `public const int`,
shared by `Contracts/` and `Data/`. `DateTimeOffset` for all timestamps. Money is `decimal` plus
a separate `Currency` string — no `Money` value object — rounded 2dp `AwayFromZero`. Identity
fields other services hold are immutable.

**EF Core.** One `IEntityTypeConfiguration<T>` per entity via `ApplyConfigurationsFromAssembly`.
Explicit snake_case `ToTable`. `HasPrecision(18, 2)` on money. `UseSnakeCaseNamingConvention()`
on the context. Name unique indexes through a `public const` — the exception handler branches
on it.

- **Optimistic concurrency is mandatory on every entity:**
  `builder.Property<uint>("xmin").IsConcurrencyToken().ValueGeneratedOnAddOrUpdate();`
  `UseXminAsConcurrencyToken()` **does not exist in Npgsql 10**. This emits no DDL.
- **Never `HasDefaultValue` on a non-nullable `bool`** — EF's sentinel is `false`, so an explicit
  `false` is dropped from the INSERT and silently comes back as the default.

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

**Errors.** `IExceptionHandler` → `Results.Problem(...)` → `application/problem+json` with a
`correlationId` extension. Classification, in switch order:

| Exception | Status |
|---|---|
| `OperationCanceledException` **and** `RequestAborted` | not handled — `LogDebug`, `return false` |
| `BadHttpRequestException` | **`badRequest.StatusCode`** — never hardcode 400; an oversized body is 413 |
| `DbUpdateConcurrencyException` | 409 (must precede `DbUpdateException`) |
| `DbUpdateException` + SQLSTATE `23505` | 409, message keyed on the **constraint name** |
| `DbUpdateException` + SQLSTATE `22001` / `22003` | 400 |
| `ArgumentException` | 400 |
| anything else | 500 |

Three invariants: **(1)** a client error is never a 5xx — 5xx rates decide whether someone gets
paged; **(2)** no exception message reaches the client, so `detail` is `null` for domain 400s and
all 500s; **(3)** the handler decides severity, not EF Core — handled rejections log one `Warning`
line with no exception object, unhandled failures log `Error` *with* it. Non-5xx lines log the
whole exception chain (`DescribeForLog`, depth cap 5).
`Microsoft.EntityFrameworkCore.Update` is `"None"` in `appsettings.json`.

**Correlation ID.** Use the constant `CorrelationIdMiddleware.HeaderName`; never hardcode the
string. Adopt an inbound value or mint `Guid.NewGuid().ToString("N")`. **Inbound values are
untrusted:** accept only 1–128 characters of ASCII letters, digits and `-_.`; otherwise
**replace** with a minted id, never reject the request. Registered first, before
`UseExceptionHandler`. Inbound-only for now.

**Soft delete.** `IsActive` + `Deactivate()` + `HasQueryFilter`; `DELETE` returns `204` and never
removes a row; `includeInactive=true` applies `IgnoreQueryFilters()`. Deactivated entities 404 on
`GET`/`PUT`/`DELETE`; there is no reactivation path.

**Paging.** `page` is 1-based and clamped to `>= 1`, `size` to `1..MaxPageSize` (Catalog: 20 /
100). Response is a `<Resource>Page` record: `Items`, `Page`, `Size`, `TotalCount`. **Order by a
unique column** — Catalog uses `Sku` — or the paging window is unstable.

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
- One container per collection: `ICollectionFixture<CatalogApiFixture>` +
  `[Collection("Catalog API")]`. xunit runs a collection sequentially, which is what makes
  per-test `ResetDatabaseAsync()` safe.
- `Program.cs` must end with `public partial class Program;` — the `WebApplicationFactory` anchor.
- **Resolve the connection string lazily inside the `AddDbContext` callback.** Reading it into a
  local first freezes it before `WebApplicationFactory` applies its override, and the tests
  silently run against the developer's own database.
- **Assert failure paths as rigorously as success paths.** The missing 400/404/409 assertions are
  exactly what shipped as 500s.
- **Structural conventions get automated guards** — a convention enforced only by prose will be
  violated: `RequestContractCoverageTests`, `ContractSchemaAlignmentTests`,
  `ComposeConfigurationTests`, `TestHostIsolationTests`, `Directory.Build.targets`,
  `scripts/verify-db-isolation.sh`.
- **Build and run the tests before declaring a task complete.** Report actual numbers, not
  "tests pass".
- Clean up any rows a smoke or E2E check creates.

Further harness gotchas: `docs/ARCHITECTURE.md` §5.3.

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

**RabbitMQ or any broker · Outbox / Inbox · Redis · Polly or any resilience library ·
OpenTelemetry · Serilog or shared logging infrastructure · shared infrastructure library ·
authentication / authorization · Kubernetes / Helm · API gateway (YARP) · CQRS · DDD tactical
patterns · event sourcing · idempotency keys · service Dockerfiles · CI pipeline.**

Also do not add at this size: repository or service layers, an `Application` layer, a mediator,
`Result<T>` monads, a mapping framework, domain-event plumbing, or a `Money` value object.

The ordering matters: retrying without idempotency keys double-reserves stock; an outbox without
a broker is dead weight; CQRS without read pressure is ceremony.

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

**Catalog is the reference for Stock and Ordering.** Before copying a convention, verify it still
fits the target service; do not duplicate Catalog-specific behaviour blindly.

**Must change per service:** the connection-string key (`"Catalog"`), all `Catalog*` type names,
the constraint names in `UniqueViolationDetail`, conflict-message wording, the entity under test
in `ContractSchemaAlignmentTests`, and the port / database / role.

**Needs judgement, not copying:**

- **Soft delete may not fit.** A product retires; a stock row should not be soft-deleted and an
  order must never vanish from a list.
- **`xmin` is low-stakes on `Product`, critical on `StockItem`**, where it prevents overselling.
  Copy the mechanism; test it far harder.
- **Domain guards differ.** Stock must enforce `QuantityOnHand - Reserved >= 0` — business logic,
  not a copied guard clause.

Catalog is single-entity CRUD and exercises nothing distributed. Stock and Ordering will need what
it lacks: a typed `HttpClient` plus interface seam, outbound correlation-id propagation, real
nested request DTOs, order-line snapshotting, compensation, and a sequence for `OrderNumber`.
Detail in `docs/ROADMAP.md`.

**When a convention proves wrong for the second service, fix it in Catalog too.** Divergent
conventions across three services are worse than one imperfect convention applied consistently.
