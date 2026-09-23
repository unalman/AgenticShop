# Architecture

Design detail behind the rules in `AGENTS.md`. Rationale for individual choices is in
`DECISIONS.md`; open and resolved defects are in `KNOWN-ISSUES.md`; the phase sequence is in
`ROADMAP.md`.

Everything here describes the verified current state. Where something is planned but not
built, it is marked as such.

---

## 1. Goals and shape

AgenticShop is an educational backend whose purpose is to reach a distributed architecture
*by feeling each problem first*. That intent governs the design: capabilities are added when
the previous phase creates the problem that justifies them, not because they are best practice
in the abstract.

Three independently deployed ASP.NET Core hosts communicating over HTTP only:

| Service | Port | Database | Role | Status |
|---|---|---|---|---|
| Catalog | 5081 | `agenticshop_catalog` | `catalog_svc` | **implemented** |
| Stock | 5082 | `agenticshop_stock` | `stock_svc` | not created |
| Ordering | 5083 | `agenticshop_ordering` | `ordering_svc` | not created |

Ports are fixed in each service's `Properties/launchSettings.json` so cross-service
configuration never drifts. In Phase 0 only PostgreSQL is containerised; the APIs run on the
host via `dotnet watch` for a fast inner loop and easy debugging. Service Dockerfiles and a
`full` compose profile arrive in Phase 1.

Two invariants are designed to survive every later change, because they are what make the
eventual distribution real rather than cosmetic:

- **One database per service**, owned by that service alone, with its own EF migrations.
  Cross-service references are `Guid` ids resolved over HTTP — never foreign keys or joins.
- **Orders snapshot `ProductName` and `UnitPrice`** so a later catalog edit cannot rewrite
  order history. (Not yet implemented; Ordering does not exist.)

---

## 2. Boundary enforcement

The boundary is enforced twice, mechanically. Neither depends on anyone remembering a
convention.

### 2.1 Build time

`Directory.Build.targets` defines an `EnforceServiceBoundaries` target that fails the build
if any project under `src/` holds a `ProjectReference` to another project under `src/`. It
runs `BeforeTargets="BeforeBuild"`, applies to every current and future service
automatically, and exempts `tests/`.

The error message names both projects and states the remedy — define the contract in the
consuming service as an interface plus a typed `HttpClient`.

Verified by creating throwaway `src/AgenticShop.ProbeA` → `ProbeB` projects: the build failed
with the actionable message, and `ProbeB` alone built clean, confirming no false positive.
Both were deleted afterwards.

**Limitation:** the guard identifies services by the `/src/` path segment. Restructuring the
repository layout silently disables it.

### 2.2 Runtime

Each service connects with its own non-superuser role that **owns** its database.
`docker/postgres/init-dbs.sh`:

1. creates the role — `LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE`
2. creates the database with `OWNER <role>`, or asserts ownership if it already exists
3. `REVOKE ALL ON DATABASE <db> FROM PUBLIC`, then `GRANT CONNECT, TEMP` to the role only
4. `ALTER SCHEMA public OWNER TO <role>` in that database

Ownership is used instead of `GRANT` plus `ALTER DEFAULT PRIVILEGES` because since
PostgreSQL 15 the `public` schema is owned by `pg_database_owner`. Making the role the
database owner therefore grants exactly what EF Core migrations need — the ability to create
tables — with no grant plumbing at all.

The `agenticshop` superuser exists only to bootstrap. No application uses it.

The script is idempotent and self-healing: it refreshes role passwords and re-asserts
ownership on re-run. It executes **only when the data volume is empty**, so applying a change
requires `docker compose down -v`, which destroys all local data.

### 2.3 Verification

`scripts/verify-db-isolation.sh`, mounted read-only into the container:

```powershell
docker compose exec db bash /usr/local/bin/verify-db-isolation
```

**22 checks**, exiting non-zero on any failure:

1. each role reaches its own database (3)
2. no role reaches either other database (6)
3. no role has `rolsuper`, `rolcreatedb` or `rolcreaterole` (9)
4. each database is owned by the matching role (3)
5. the owning role can create and drop a table, which migrations require (1)

Run it after any change to `init-dbs.sh` or `docker-compose.yml`.

---

## 3. Build and dependency management

- **`Directory.Build.props`** — `TargetFramework net10.0`, `LangVersion latest`, `Nullable`,
  `ImplicitUsings`, **`TreatWarningsAsErrors`**, `EnforceCodeStyleInBuild`. One place keeps
  all nine eventual projects on the same TFM.
- **`Directory.Build.targets`** — the boundary guard above.
- **`Directory.Packages.props`** — central package management. Every `PackageReference` omits
  `Version`. `CentralPackageTransitivePinningEnabled` is on; see `DECISIONS.md`.
- **`global.json`** — SDK `10.0.400`, `rollForward: latestFeature`. Rejects a machine with
  only `10.0.1xx`.
- **`dotnet-tools.json`** at the repository root pins `dotnet-ef` 10.0.12. Both
  `./dotnet-tools.json` and `./.config/dotnet-tools.json` are valid manifest locations;
  `dotnet tool restore` finds either.
- **`AgenticShop.slnx`** — the XML solution format, which is the SDK 10 default. Chosen over
  `.sln` because `.sln` embeds project GUIDs and is a merge-conflict magnet when projects are
  added in parallel. Solution folders: `src`, `tests`.
- **`.gitattributes`** — `*.sh text eol=lf`. A CRLF shell script fails inside the PostgreSQL
  container with a `\r`-suffixed interpreter error. Verified `CR=0` after authoring on Windows.
- **`.editorconfig`** — the generated default. All rules are `:silent` or `:suggestion`, so
  `EnforceCodeStyleInBuild` cannot turn a style preference into a build failure.

`TreatWarningsAsErrors` has a sharp edge worth knowing: a third-party package marking an API
obsolete becomes a build break on upgrade. That is intended — it is how the obsolete
`PostgreSqlBuilder()` constructor was caught — but it means package bumps occasionally require
code changes.

### Local CLI

`dotnet` output on this machine is Turkish-localised. Filter build and test logs on `Hata`
(error), `Uyarı` (warning), `Başarılı` (passed), `Başarısız` (failed). PowerShell
`Select-String` against those strings is unreliable; redirect to a file and read it.

---

## 4. Catalog reference implementation

### 4.1 Project structure

```
src/AgenticShop.Catalog/
├── Program.cs              composition root
├── Contracts/              request/response records + IRequestContract marker
├── Data/                   CatalogDbContext, ProductConfiguration, Migrations/
├── Domain/                 Product — invariants only, no EF, no ASP.NET
├── Endpoints/              ProductEndpoints
├── Errors/                 CatalogExceptionHandler
├── Middleware/             CorrelationIdMiddleware
├── Validation/             DataAnnotationValidationFilter
└── Properties/launchSettings.json
```

One project per service. No `Api`/`Core`/`Application` split, no repository, service or
mediator layer, no `Result<T>` monad, no mapping framework. Handlers call the `DbContext`
directly. `Domain/` must not reference EF Core or ASP.NET Core types.

`Contracts/` depends on `Domain/` — for the length constants and for `ProductResponse.From` —
which is the correct inward direction.

### 4.2 Entities

`Domain/Product.cs` is the template.

- `private set` on every property; a `private` parameterless constructor marked as EF-only.
- `static Create(...)` assigns `Id = Guid.NewGuid()` and both timestamps from one
  `DateTimeOffset.UtcNow` read, so `CreatedAtUtc == UpdatedAtUtc` on a new entity.
- Explicit mutation methods (`Update`, `Deactivate`) instead of public setters.
- **Guards run before any mutation.** `Update_LeavesTheProductUntouchedWhenAGuardRejects`
  asserts the entity is byte-identical after a rejected call — otherwise EF would persist a
  half-applied change on the next save.
- Length limits are `public const int` — `SkuMaxLength = 64`, `NameMaxLength = 200`,
  `DescriptionMaxLength = 2000`, `CurrencyLength = 3`, `DefaultCurrency = "USD"` — so
  `Contracts/` and `Data/` reference one number instead of three copies.
- `DateTimeOffset` for all timestamps, never `DateTime`. Npgsql maps it to
  `timestamp with time zone`, which is unambiguous across services.
- Money is `decimal` plus a separate `Currency` string. **No `Money` value object** — it would
  be a DDD construct the project has deferred.
- Money rounds to 2dp with `MidpointRounding.AwayFromZero`, matching `numeric(18,2)`.
- Currency is normalised with **`ToUpperInvariant`**, not `ToUpper`. This machine's culture is
  Turkish, where `ToUpper` maps `i` to a dotted capital İ that is not an ASCII letter — so
  `"ils"` would fail validation under `ToUpper` and pass under `ToUpperInvariant`. A unit test
  pins this.
- Currency is validated as exactly three ASCII letters per ISO 4217. The registry of assigned
  codes is deliberately not embedded: it changes as codes are added and withdrawn, and a stale
  allow-list would reject legitimate values while giving false confidence.
- `Sku` is immutable after creation, because it is the identity key other services hold;
  changing it would silently orphan their references.
- Whitespace is trimmed; a blank `Description` normalises to `null` rather than `""`.

### 4.3 EF Core configuration

`Data/ProductConfiguration.cs`, one `IEntityTypeConfiguration<T>` per entity, discovered by
`ApplyConfigurationsFromAssembly` in the `DbContext`.

- Explicit snake_case `ToTable("products")`.
- `HasPrecision(18, 2)` on money → `numeric(18,2)`.
- Unique indexes are named through a `public const`
  (`UniqueSkuIndexName = "ix_products_sku"`) **because the exception handler branches on the
  name**. Leaving it to EF's convention would let a rename silently break the 409 message.

**Optimistic concurrency.** Every entity carries a PostgreSQL `xmin` token:

```csharp
builder.Property<uint>("xmin").IsConcurrencyToken().ValueGeneratedOnAddOrUpdate();
```

`UseXminAsConcurrencyToken()` **does not exist in Npgsql 10** — verified against the assembly
binary, not just the XML docs. Instead `NpgsqlPostgresModelFinalizingConvention` detects any
`uint` concurrency token generated `OnAddOrUpdate` and maps it to the `xmin` system column.

The migration file contains an `AddColumn<uint>("xmin", type: "xid")` operation, but
**`NpgsqlMigrationsSqlGenerator.SystemColumnNames` suppresses it when generating SQL**.
Confirmed two ways: `dotnet ef migrations script` contains no occurrence of `xmin`, and
`information_schema.columns` on the live database returns 0 rows for it. Inserts carry
`RETURNING xmin;` so EF can refresh the token.

Without a concurrency token two overlapping writes both succeed and the second silently
discards the first. On `Product` that is a lost price update; on `StockItem` it is overselling.

**Never `HasDefaultValue` on a non-nullable `bool`.** EF's sentinel for `bool` is `false`, so
an explicit `false` is indistinguishable from "unset": EF omits the column from the INSERT and
the database default is applied instead. A product created inactive would silently come back
active. `ProductConfiguration` carries a comment explaining this; the default was removed in
migration `AddXminConcurrencyAndDropIsActiveDefault`.

**Query filter.** `HasQueryFilter(p => p.IsActive)` makes deactivated rows invisible by
default. No supporting index is needed: with the large majority of rows active a sequential
scan is optimal and an index would be ignored.

`UseSnakeCaseNamingConvention()` is applied on the `DbContextOptionsBuilder`, not on the
Npgsql sub-builder. It also renames EF's own `__EFMigrationsHistory` columns to
`migration_id` / `product_version`, which surprises anyone writing raw SQL against it.

The `DbContext` is a primary-constructor one-liner over `DbContextOptions<T>`.

### 4.4 Migrations

Per service, under `Data/Migrations/`, namespace `<Service>.Data.Migrations`.

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet ef migrations add <Name> -p src/AgenticShop.Catalog -s src/AgenticShop.Catalog
dotnet ef database update       -p src/AgenticShop.Catalog -s src/AgenticShop.Catalog
dotnet ef migrations script     -p ... -s ...   # inspect before applying anything non-trivial
```

`ASPNETCORE_ENVIRONMENT=Development` is required so `appsettings.Development.json` supplies
the connection string. There is **no design-time factory**; EF resolves the context through
the app host, intercepting `Build()`, so code after it does not run at design time.

Current migrations: `20260923143431_InitialCatalog`,
`20260923154230_AddXminConcurrencyAndDropIsActiveDefault`.

Applied automatically at startup **only in `Development`** via `MigrateAsync()`. That guard is
what avoids the multi-replica migration race — EF Core documents concurrent `Migrate` as
unsafe. Production applies migrations explicitly via CLI.

Migrations run as the service role (`catalog_svc`), which works precisely because that role
owns the database.

A failed `DbCommand` line reading `Failed executing DbCommand` during `database update` is
EF's benign probe for the existence of the history table, not a failure.

### 4.5 Endpoints

`Endpoints/ProductEndpoints.cs`:

- One `static class <Resource>Endpoints` exposing a
  `Map<Resource>Endpoints(this IEndpointRouteBuilder)` extension called from `Program.cs`.
- `MapGroup("/api/v1/<resource>")` — versioned by URL prefix — with `.WithTags(...)`.
- **Every group registers `.AddEndpointFilter<DataAnnotationValidationFilter>()`.**
- Handlers are `private static async Task<IResult>` taking the `DbContext` and a
  `CancellationToken`.
- Every route declares `.WithName`, `.WithSummary` and `.Produces<T>()` /
  `.ProducesProblem(...)` / `.ProducesValidationProblem()` so the OpenAPI document is accurate.
- `.AsNoTracking()` on reads; writes load a tracked entity, call a domain method, then
  `SaveChangesAsync`.
- `201` + `Location` on create, `200` + body on update, `204` on delete, `404` when missing,
  `409` on conflict.

Catalog routes:

```
GET    /api/v1/products?page=&size=&includeInactive=
GET    /api/v1/products/{id:guid}
POST   /api/v1/products
PUT    /api/v1/products/{id:guid}
DELETE /api/v1/products/{id:guid}
GET    /health
```

`/health`, `/openapi/v1.json` and `/scalar/v1` are mapped in `Program.cs`. **The latter two
are Development-only** — a publicly reachable Scalar UI is an interactive attack surface.

Pipeline order in `Program.cs` matters: `UseCorrelationId()` runs **before**
`UseExceptionHandler()`, so a failure raised by later middleware still carries a correlation
id and the handler can read it.

`Program.cs` ends with `public partial class Program;` — the `WebApplicationFactory<Program>`
anchor for integration tests.

### 4.6 Validation

Two layers, deliberately overlapping: DataAnnotations on `Contracts/` records guard the
boundary; domain guards protect invariants regardless of entry point.

**Minimal APIs do not validate body DTOs automatically.** Automatic model validation is an
MVC `[ApiController]` behaviour. Without `DataAnnotationValidationFilter` the attributes in
`Contracts/` are decorative — this produced a real HTTP 500 before the filter existed.

The filter's contract:

- Candidates are identified by the **`IRequestContract` marker interface**, not by namespace
  or type name, so injected services are skipped without an allow-list and a DTO cannot be
  missed by living in the wrong folder.
- `Validator.TryValidateObject` **does not cascade**, so the filter recurses itself into
  nested objects *and* collections, reporting full member paths: `Lines[0].Quantity`,
  `Groups[1].Cells[0].Quantity`, `Rows[1][0].Quantity`.
- A collection is traversed **by element**, not by property. Reflecting over a `List<T>`
  yields `Count` and `Capacity`, so property-only recursion silently validates nothing below
  the first level. This was a real bug found while writing the tests.
- Class-level attributes report no member names; they are attached to the object's own path
  rather than dropped.
- A reference cycle is guarded by a `HashSet<object>` using `ReferenceEqualityComparer`.
- Leaf types are not descended into: primitives, enums, `string`, `decimal`, `DateTime`,
  `DateTimeOffset`, `DateOnly`, `TimeOnly`, `TimeSpan`, `Guid`, `Uri`. `string` is checked
  **before** the `IEnumerable` branch, because strings are enumerable.
- Failures return `Results.ValidationProblem(...)`.
- The marker's own gap — forgetting to implement it — is closed by
  `RequestContractCoverageTests`, which reflects over `Contracts/` and fails the build.

Catalog has **no nested request DTO**. The cascade is proven by synthetic contracts in
`DataAnnotationValidationFilterTests`; that suite is the specification Stock and Ordering
inherit, and Ordering's `CreateOrderRequest` will be the first real one.

### 4.7 Error handling

`Errors/CatalogExceptionHandler.cs` implements `IExceptionHandler` and always answers with
`Results.Problem(...)` → `application/problem+json`, RFC 9457, carrying a `correlationId`
extension. That shared shape is why no `AgenticShop.Shared` project is needed.

Classification, in switch order:

| Exception | Status | Detail |
|---|---|---|
| `OperationCanceledException` **and** `RequestAborted` | *not handled* | `LogDebug`, `return false` |
| `BadHttpRequestException` | **`badRequest.StatusCode`** | `null` |
| `DbUpdateConcurrencyException` | 409 | reload hint |
| `DbUpdateException` + `23505` | 409 | keyed on constraint name |
| `DbUpdateException` + `22001` / `22003` | 400 | too long / out of range |
| `ArgumentException` | 400 | `null` |
| anything else | 500 | `null` |

`DbUpdateConcurrencyException` must precede `DbUpdateException` because it derives from it.
`BadHttpRequestException` uses its own `StatusCode` rather than a hardcoded 400 because an
oversized body is a 413.

Three invariants, all test-guarded:

1. **A client error is never a 5xx.** 5xx rates and error logs are how operators decide
   whether to page someone, so misclassifying bad input creates false alarms any caller can
   generate at will.
2. **No exception message reaches the client.** `detail` is `null` for domain 400s and all
   500s. Those messages carry internal parameter names and are formatted with the *server's*
   culture — on this host `-5.00` renders as `-5,00`.
3. **The handler decides severity, not EF Core.** Handled rejections log one `Warning` line
   with no exception object; unhandled failures log `Error` *with* the exception object, so
   the full inner chain and stack traces survive.

**Log content.** Non-5xx lines walk the whole exception chain via `DescribeForLog`, capped at
depth 5 and joined with `->`. Both ends matter: for a binding failure the outer message names
the parameter that failed and only the inner one says why; for a database failure the reverse
is true. Logging only the outermost exception reports nothing useful, because EF's wrapper
says only "see the inner exception for details". `PostgresException` is formatted specially to
surface `SqlState`, `MessageText`, and `table=` / `column=` / `constraint=` when present;
`MessageText` is used rather than `Message`, which re-prefixes the same SQLSTATE.

`Microsoft.EntityFrameworkCore.Update` is set to `"None"` in `appsettings.json`. EF logs every
`SaveChanges` failure at Error with a full stack trace before rethrowing and cannot know
whether it was handled, so a routine duplicate-key 409 used to emit roughly 40 lines. Severity
classification belongs to the handler because only it knows whether the error was handled.

Client cancellation returns `false`, handing the exception back to the framework: there is no
client left to receive a response, and logging it as an error would make every aborted request
look like an incident. The arm is guarded on `RequestAborted`, so a genuine internal timeout
still counts as a fault.

### 4.8 Correlation ID

`Middleware/CorrelationIdMiddleware.cs`:

- Header name is the constant `CorrelationIdMiddleware.HeaderName` (`X-Correlation-Id`).
- An inbound value is adopted; otherwise `Guid.NewGuid().ToString("N")` — 32 lowercase hex.
- **The inbound value is untrusted.** It is echoed into a response header, embedded in the
  ProblemDetails body, assigned to `HttpContext.TraceIdentifier` and written to every log line
  for the request. It is accepted only if it is 1–128 characters (`MaxLength`) and contains
  nothing outside ASCII letters, digits and `-_.` — the characters that appear in GUIDs, W3C
  trace ids and conventional prefixed tokens.
- An unacceptable value is **replaced**, not rejected: a malformed correlation id does not
  make the underlying request invalid, and refusing the call would turn a tracing concern into
  an availability one.
- Also stored in `HttpContext.Items[HeaderName]`; read back with the `GetCorrelationId()`
  extension, which falls back to `TraceIdentifier`.
- Registered first in the pipeline so later middleware failures still carry it.

Propagation is currently **inbound only**. Nothing forwards the header on outbound calls
because no service makes any yet. See `ROADMAP.md`.

### 4.9 Soft delete

`IsActive` on the entity, `Deactivate()` as the only way to clear it, `HasQueryFilter` making
deactivated rows invisible. `DELETE` returns `204` and never removes a row, so history stays
queryable for orders that already reference the product. `includeInactive=true` applies
`IgnoreQueryFilters()`.

Consequences: `GET`, `PUT` and `DELETE` on a deactivated entity all return `404`, and there is
**no reactivation path**.

### 4.10 Paging

`page` is 1-based and clamped to `>= 1`; `size` to `1..MaxPageSize`. Catalog uses
`DefaultPageSize = 20`, `MaxPageSize = 100`. The response is a `<Resource>Page` record with
`Items`, `Page`, `Size`, `TotalCount`, echoing the clamped values.

**Order by a unique column** — Catalog uses `Sku`. Ordering by a non-unique column such as
`Name` makes the paging window unstable: rows can be skipped or repeated across pages.

`CountAsync` and `ToListAsync` are two queries outside a transaction, so `TotalCount` can
disagree with the page under concurrent writes. Standard for offset paging and accepted; see
`KNOWN-ISSUES.md`.

---

## 5. Testing architecture

Two projects per service — `<Service>.UnitTests` and `<Service>.IntegrationTests` — rather
than one shared test project, so fixtures do not multiply inside a single assembly.

| | Unit | Integration |
|---|---|---|
| Scope | domain rules, validation filter, error classification | real API over HTTP against real PostgreSQL |
| I/O | none | one Testcontainers container per collection |
| Docker | not needed | required |

Current counts: **99 unit, 39 integration, 138 total, all passing.**

### 5.1 Why Testcontainers and never a mocked DbContext

Mocks pass while the real query fails. Concretely, only a real PostgreSQL could have surfaced:

- the duplicate-SKU `409` — needs the actual unique index to raise SQLSTATE `23505`
- the over-length-SKU `500` — needs a real `varchar(64)` to reject the insert
- the `xmin` concurrency token — a PostgreSQL system column with no in-memory equivalent
- the soft-delete query filter — real SQL translation

### 5.2 Harness design

- `CatalogApiFactory : WebApplicationFactory<Program>` calls `builder.UseEnvironment("Development")`
  **explicitly** rather than inheriting the framework default, because the suite depends on it:
  `Program.cs` runs `MigrateAsync()` only when `IsDevelopment()`. It also overrides
  `ConnectionStrings:Catalog` through `ConfigureAppConfiguration`.
- `CatalogApiFixture : IAsyncLifetime` owns one `PostgreSqlContainer` built from
  `TestPostgreSql.Image`, exposed as `ICollectionFixture` via `CatalogApiCollection` with
  `[Collection("Catalog API")]`. xunit runs a collection sequentially, which is what makes
  per-test `ResetDatabaseAsync()` safe.
- `ResetDatabaseAsync()` uses `ExecuteDeleteAsync` with `IgnoreQueryFilters()` so soft-deleted
  rows are removed too. It does not reset sequences.
- `CreateScope()` exposes the host's services so a test can open independent units of work
  against the same database — which is what an optimistic-concurrency conflict needs.
- Disposal order is `Client` → factory → container.
- Tests that assert on `ProblemDetails` deserialise the response body rather than depending on
  an internal result type.

### 5.3 Gotchas that have already bitten

- **The connection string must be resolved lazily inside the `AddDbContext` callback.**
  `WebApplicationFactory.ConfigureAppConfiguration` applies its override *during* `Build()`.
  Reading the value into a local beforehand freezes it, and the tests then run silently against
  the developer's own compose database while the Testcontainers container sits unused. This
  happened. `TestHostIsolationTests` now fails the suite if it recurs; it can also be proven by
  running the integration tests with `docker compose stop db`.
- `ProblemHttpResult.ExecuteAsync` resolves `ILoggerFactory` and `JsonOptions` from
  `HttpContext.RequestServices`, so a bare `DefaultHttpContext` throws. Build one with
  `.AddOptions().AddLogging()`.
- `PostgreSqlBuilder` requires the image argument in Testcontainers 4.x; the parameterless
  constructor is obsolete and, with `TreatWarningsAsErrors`, a build error.
- `PostgresException` exposes its error fields read-only, so a constraint name can only be
  supplied through the full constructor.
- `ValidationProblemHttpResult` is not accessible from a test assembly; assert on the
  serialised wire body instead.
- `Validator.TryValidateObject` reports only the first failure per property when `[Required]`
  is among them, so aggregation tests must use two independent rules.
- `TestPostgreSql.Image` is the single declaration of the PostgreSQL image tag;
  `docker-compose.yml` states its own and `ComposeConfigurationTests` fails if they diverge.

### 5.4 Structural guards

A convention enforced only by prose will be violated. These fail the build or the suite:

| Guard | Protects |
|---|---|
| `Directory.Build.targets` | no cross-service `ProjectReference` (build time) |
| `RequestContractCoverageTests` | every `*Request` in `Contracts/` implements `IRequestContract`, and the check does not pass vacuously |
| `ContractSchemaAlignmentTests` | DTO length limits equal EF column limits; `[Range]` respects `numeric(18,2)` |
| `ComposeConfigurationTests` | compose image matches `TestPostgreSql.Image`; port is loopback-only; no literal credentials; the isolation script is mounted |
| `TestHostIsolationTests` | the app under test really uses the Testcontainers database |
| `scripts/verify-db-isolation.sh` | PostgreSQL role isolation, 22 checks |

`ContractSchemaAlignmentTests` reads limits from the EF model rather than restating them, so
it tracks `ProductConfiguration` instead of a second copy that could itself drift. That is the
guard which would have prevented the original over-length-SKU 500.
