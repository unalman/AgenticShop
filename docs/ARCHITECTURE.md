# Architecture

**Global** design detail behind the rules in `AGENTS.md`: topology, boundary enforcement, the
build system, the conventions every service shares, and the testing architecture.

Service-specific design lives with the service:

- `src/AgenticShop.Catalog/docs/ARCHITECTURE.md`
- `src/AgenticShop.Stock/docs/ARCHITECTURE.md`
- `src/AgenticShop.Ordering/docs/ARCHITECTURE.md`

Rationale for individual choices is in `DECISIONS.md` (and each service's own); open and
resolved defects in `KNOWN-ISSUES.md` (likewise); the phase sequence in `ROADMAP.md`.

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
| Stock | 5082 | `agenticshop_stock` | `stock_svc` | **implemented** |
| Ordering | 5083 | `agenticshop_ordering` | `ordering_svc` | **implemented** |

Catalog is the reference implementation, Stock is the worked example of adapting it, and Ordering is
the test of whether the conventions survive a service that orchestrates rather than owns a single
resource. Each service's deliberate divergences are the evidence that the conventions transfer
rather than merely copy, and are tabulated in that service's own `docs/ARCHITECTURE.md` — Stock §8,
Ordering §10. The tables are the count; do not restate a number here. §4 below holds only what all
three genuinely share.

Ports are fixed in each service's `Properties/launchSettings.json` so cross-service
configuration never drifts. In Phase 0 only PostgreSQL is containerised; the APIs run on the
host via `dotnet watch` for a fast inner loop and easy debugging. Service Dockerfiles and a
`full` compose profile arrive in Phase 1.

Two invariants are designed to survive every later change, because they are what make the
eventual distribution real rather than cosmetic:

- **One database per service**, owned by that service alone, with its own EF migrations.
  Cross-service references are `Guid` ids resolved over HTTP — never foreign keys or joins.
- **Orders snapshot `ProductName` and `UnitPrice`** so a later catalog edit cannot rewrite
  order history. Implemented, and pinned by
  `ALineSnapshotsTheCatalogSoALaterEditCannotRewriteOrderHistory`, which changes the product after
  the order exists and asserts the order did not move.

---

## 2. Boundary enforcement

The boundary is enforced twice, mechanically. Neither depends on anyone remembering a
convention.

### 2.1 Build time

`Directory.Build.targets` defines an `EnforceServiceBoundaries` target that fails the build
if any project under `src/` holds a `ProjectReference` to another project under `src/`. It
runs `BeforeTargets="BeforeBuild"`, applies to every current and future service
automatically, and exempts `tests/`.

**One exemption, by name.** `Directory.Build.props` declares
`<AgenticShopSharedProject>AgenticShop.Shared</AgenticShopSharedProject>` and the target skips a
reference whose filename matches it. That is the whole of the relaxation: it admits one shared
infrastructure library and nothing else, so a reference between two services is still a build
error. Declaring the name in `Directory.Build.props` rather than in the condition means renaming
the library is a one-line change in one place rather than an edit to guard logic. The shared project
is itself under `src/`, so the guard still applies *to* it — it may not reference any service, and
the dependency only ever points one way.

The error message names both projects and states the remedy — define the contract in the
consuming service as an interface plus a typed `HttpClient`. The guard was verified to fire, and
verified not to false-positive, using throwaway probe projects; see `DECISIONS.md`. It was
re-verified after the exemption was added, by pointing Catalog at Stock and confirming the build
still failed.

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

## 4. Shared service conventions

These apply to every service identically. Four of them are now *literally* shared code rather than
convention: `DataAnnotationValidationFilter`, `CorrelationIdMiddleware`, `IRequestContract` and the
`ProblemDetailsExceptionHandler` skeleton all live in `src/AgenticShop.Shared/` and are referenced,
not copied. The rest — entity shape, EF configuration, endpoint style, the four error invariants —
are conventions each service implements itself. Service-specific application of any of them lives in
`src/<Service>/docs/ARCHITECTURE.md`.

### 4.1 Project structure

```
src/AgenticShop.<Service>/
├── Program.cs              composition root
├── Contracts/              request/response records, implementing the shared IRequestContract
├── Data/                   <Service>DbContext, <Entity>Configuration, Migrations/
├── Domain/                 entities and invariants — no EF, no ASP.NET
├── Endpoints/              one static class per resource
├── Errors/                 <Service>ExceptionHandler : ProblemDetailsExceptionHandler
├── Clients/                orchestrators only: an interface + typed HttpClient per downstream
└── Properties/launchSettings.json
```

Two folders a service used to have are now gone from it. `Middleware/` and `Validation/` live in
`src/AgenticShop.Shared/`, alongside `Contracts/IRequestContract` and the
`ProblemDetailsExceptionHandler` base class:

```
src/AgenticShop.Shared/
├── Contracts/IRequestContract.cs
├── Errors/ProblemDetailsExceptionHandler.cs, ExceptionClassification.cs
├── Logging/ServiceLogging.cs
├── Middleware/CorrelationIdMiddleware.cs
└── Validation/DataAnnotationValidationFilter.cs
```

The shared project is not a service — no endpoints, no `DbContext`, no domain, no configuration, no
`appsettings.json` — and it may not reference one. See §2.1 for how the boundary guard permits
exactly this and nothing else.

One project per service. No `Api`/`Core`/`Application` split, no repository, service or
mediator layer, no `Result<T>` monad, no mapping framework. Handlers call the `DbContext`
directly. `Domain/` must not reference EF Core or ASP.NET Core types.

`Contracts/` depends on `Domain/` — for the limit constants and the `From(...)` projections —
which is the correct inward direction.

A service that calls another service adds a `Clients/` folder holding an interface plus a typed
`HttpClient` implementation per downstream service. **Catalog and Stock have none**, because neither
makes an outbound call and Stock's absence is asserted by `LeafServiceBoundaryTests`. Ordering has
two, plus the consumer-owned response records they deserialise into, a `DelegatingHandler` that
propagates the correlation id, and the shared send-and-classify plumbing — see
`src/AgenticShop.Ordering/docs/ARCHITECTURE.md` §6.

### 4.2 Entities

- `private set` on every property; a `private` parameterless constructor marked as EF-only.
- A `static Create(...)` factory assigns `Id = Guid.NewGuid()` and all timestamps from one
  `DateTimeOffset.UtcNow` read, so `CreatedAtUtc == UpdatedAtUtc` on a new entity. Ordering's `Order`
  diverges on both counts and for one reason: its id is supplied by the caller because Stock's
  reservations carry it and are created before the order row exists, and it has no `UpdatedAtUtc`
  because it is write-once.
- Explicit mutation methods instead of public setters.
- **Guards run before any mutation**, so a rejected call leaves the entity byte-identical —
  otherwise EF would persist a half-applied change on the next save. Each service has a test
  asserting exactly this.
- Limits are `public const` on the entity, so `Contracts/` and `Data/` reference one number
  instead of three copies.
- `DateTimeOffset` for all timestamps, never `DateTime`. Npgsql maps it to
  `timestamp with time zone`, which is unambiguous across services.
- **Money is `decimal` plus a separate `Currency` string**, never a `Money` value object. The
  amount is rounded to 2dp with `MidpointRounding.AwayFromZero` (`Product.NormalizePrice`,
  `OrderLine.NormalizePrice`) and mapped by `HasPrecision(18, 2)` to `numeric(18,2)`; the currency
  is a three-letter ISO 4217 code, trimmed and upper-cased on the way in. Catalog and Ordering both
  carry money — `Product.Price`, `OrderLine.UnitPrice` — and Ordering additionally refuses an order
  whose lines disagree on currency (`MixedCurrencyException` → 400, raised while the placement is
  still pure reads, so it costs no compensation). **Stock has no money at all**: do not add fields
  a service does not need. `Order.CurrencyLength` mirrors Catalog's constant rather than sharing
  it, because no type crosses a service boundary (§2.1). Catalog's application of the rule — the
  `"USD"` default, `ToUpperInvariant` rather than `ToUpper`, and ISO 4217 validation by format —
  is in `src/AgenticShop.Catalog/docs/ARCHITECTURE.md` → "Money and currency"; the reasoning in
  `src/AgenticShop.Catalog/docs/DECISIONS.md` → "`decimal` plus a `Currency` column, no `Money`
  value object".
- **Derived values are computed properties, never stored**, and marked `builder.Ignore(...)`
  so a second copy of the truth cannot drift. Stock's `Available = QuantityOnHand - Reserved`
  is the template.
- Identity fields that other services hold are immutable after creation.
- **A domain rejection that must map to 409 gets its own exception type carrying typed
  properties.** `ArgumentException` maps to 400, so it cannot express a conflict. The handler
  builds client-facing text from those properties, never from `Message`, so rewording an
  exception cannot silently change the public contract.

### 4.3 EF Core configuration

One `IEntityTypeConfiguration<T>` per entity, discovered by `ApplyConfigurationsFromAssembly`
in the `DbContext`, which is a primary-constructor one-liner over `DbContextOptions<T>`.

- Explicit snake_case `ToTable(...)`. `UseSnakeCaseNamingConvention()` is applied on the
  `DbContextOptionsBuilder`, not the Npgsql sub-builder. It also renames EF's own
  `__EFMigrationsHistory` columns to `migration_id` / `product_version`, which surprises anyone
  writing raw SQL against it.
- Unique indexes are named through a `public const` **because the exception handler branches on
  the name**. Leaving it to EF's convention would let a rename silently break a 409 message.
- `HasPrecision(18, 2)` on money → `numeric(18,2)`.

**Optimistic concurrency is mandatory on every entity:**

```csharp
builder.Property<uint>("xmin").IsConcurrencyToken().ValueGeneratedOnAddOrUpdate();
```

`UseXminAsConcurrencyToken()` **does not exist in Npgsql 10** — verified against the assembly
binary, not just the XML docs. Instead `NpgsqlPostgresModelFinalizingConvention` detects any
`uint` concurrency token generated `OnAddOrUpdate` and maps it to the `xmin` system column.

The migration file contains an `AddColumn<uint>("xmin", type: "xid")` operation, but
**`NpgsqlMigrationsSqlGenerator.SystemColumnNames` suppresses it when generating SQL**.
Confirmed two ways in all three services: `dotnet ef migrations script` contains no occurrence of
`xmin`, and `information_schema.columns` on the live database returns 0 rows for it. Inserts
carry `RETURNING xmin;` so EF can refresh the token.

Note that the token being *declared* is not the same as it being *exercised*. Catalog's guards a
price edit, Stock's prevents overselling and is proven by parallel tests, and Ordering's is
currently inert — a Phase 0 order is written once and never updated, so no conflict can arise.
It is declared anyway because it is mandatory on every entity and Phase 2's saga will need it.

Without a concurrency token two overlapping writes both succeed and the second silently discards
the first. The stakes differ per service — a lost price edit on `Product`, overselling on
`StockItem` — which is why the testing burden differs too.

**Never `HasDefaultValue` on a non-nullable `bool`.** EF's sentinel for `bool` is `false`, so an
explicit `false` is indistinguishable from "unset": EF omits the column from the INSERT and the
database default is applied instead.

**`CHECK` constraints** are legitimate defence-in-depth, but their SQL is passed through
verbatim, so it must name columns *after* snake_case renaming. Reading them back in a test
requires `db.GetService<IDesignTimeModel>().Model` — `GetCheckConstraints()` throws against the
read-optimised runtime model.

Enum properties map with `.HasConversion<string>()` plus a `CHECK` listing the names, so state
is readable in `psql` rather than as integers.

**A foreign key within one service's own database is fine** — Stock's
`stock_reservations → stock_items` uses one, with `DeleteBehavior.Restrict` and `WithMany()`
(no navigation property, so the object graph stays flat). What is forbidden is a key crossing a
service boundary.

### 4.4 Migrations

Per service, under `Data/Migrations/`, namespace `<Service>.Data.Migrations`.

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet ef migrations add <Name> -p src/AgenticShop.<Service> -s src/AgenticShop.<Service>
dotnet ef database update       -p src/AgenticShop.<Service> -s src/AgenticShop.<Service>
dotnet ef migrations script     -p ... -s ...   # inspect before applying anything non-trivial
```

`ASPNETCORE_ENVIRONMENT=Development` is required so `appsettings.Development.json` supplies the
connection string. There is **no design-time factory**; EF resolves the context through the app
host, intercepting `Build()`, so code after it does not run at design time.

Applied automatically at startup **only in `Development`** via `MigrateAsync()`. That guard is
what avoids the multi-replica migration race — EF Core documents concurrent `Migrate` as
unsafe. Production applies migrations explicitly via CLI.

Migrations run as the service's own role, which works precisely because that role owns the
database.

A `Failed executing DbCommand` line during `database update` is EF's benign probe for the
existence of the history table, not a failure.

### 4.5 Endpoints

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

`/health`, `/openapi/v1.json` and `/scalar/v1` are mapped in `Program.cs`. **The latter two are
Development-only** — a publicly reachable Scalar UI is an interactive attack surface.

Pipeline order in `Program.cs` matters: `UseCorrelationId()` runs **before**
`UseExceptionHandler()`, so a failure raised by later middleware still carries a correlation id
and the handler can read it.

`Program.cs` ends with `public partial class Program;` — the `WebApplicationFactory<Program>`
anchor for integration tests.

**Multi-entity writes commit in a single `SaveChangesAsync`**, so EF wraps them in one
transaction and the parts cannot disagree. Stock's counter update and reservation insert rely on
this — and it is the property Phase 2's outbox will depend on, since an outbox row added to the
same context would commit atomically with both.

**Let the database reject duplicates rather than pre-checking.** `SELECT`-then-`INSERT` has a
race window under retry; a unique index does not. Catalog's unique `sku` and Stock's
`UNIQUE(order_id, stock_item_id)` are what make their create paths naturally idempotent.

### 4.6 Validation

Two layers, deliberately overlapping: DataAnnotations on `Contracts/` records guard the
boundary; domain guards protect invariants regardless of entry point.

**Every attribute on a positional record parameter carries the `[property: ...]` target** — in
`Contracts/` and in the filter's own tests, without exception. An untargeted attribute on a
positional parameter can bind to the parameter rather than to the property the compiler generates,
and `Validator.TryValidateObject` reflects over properties, so the limit would stop being enforced
with nothing to indicate it. The target costs one word.

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
- The marker's own gap — forgetting to implement it — is closed by each service's
  `RequestContractCoverageTests`, which reflects over `Contracts/` and fails the build.

**Ordering's `CreateOrderRequest` is the only real nested DTO**, and is what proved the cascade
against a real contract rather than a synthetic one. `AgenticShop.Shared.UnitTests`'s
`DataAnnotationValidationFilterTests` remains the single specification for the filter and is
deliberately not duplicated in any service.

### 4.7 Error handling

Each service has a `<Service>ExceptionHandler` deriving from the shared
`ProblemDetailsExceptionHandler`, always answering with `Results.Problem(...)` →
`application/problem+json`, RFC 9457, carrying a `correlationId` extension. The base class owns
`TryHandleAsync` — the cancellation arm, the severity decision, the logging and the ProblemDetails
shape — plus the arms that mean the same thing everywhere. A service supplies three members:
`ClassifyDomain` (virtual, defaults to `null`, so a service with no domain exceptions overrides
nothing), `UniqueViolationDetail` and `ConcurrencyDetail`.

That division is what makes the four invariants enforceable rather than merely documented: they
live in one place, so a service cannot quietly stop honouring one.

Classification, in switch order:

| Exception | Status | Detail |
|---|---|---|
| `OperationCanceledException` **and** `RequestAborted` | *not handled* | `LogDebug`, `return false` |
| `BadHttpRequestException` | **`badRequest.StatusCode`** | `null` |
| service-specific domain conflicts | 409 | built from typed properties |
| a dependency failed — **Ordering only** | 502 | `DownstreamServiceException`, `OrderPlacementIncompleteException` |
| `DbUpdateConcurrencyException` | 409 | reload hint |
| `DbUpdateException` + `23505` | 409 | keyed on constraint name |
| `DbUpdateException` + `22001` / `22003` | 400 | too long / out of range |
| `ArgumentException` | 400 | `null` |
| anything else | 500 | `null` |

`DbUpdateConcurrencyException` must precede `DbUpdateException` because it derives from it.
`BadHttpRequestException` uses its own `StatusCode` rather than a hardcoded 400 because an
oversized body is a 413. The 502 arm exists only where a dependency can fail — a 500 there would
say "we have a bug" when the truth is "we were let down", and operators page differently on those.

Four invariants, all test-guarded:

1. **A client error is never a 5xx.** 5xx rates and error logs are how operators decide
   whether to page someone, so misclassifying bad input creates false alarms any caller can
   generate at will.
2. **A server fault is never a 4xx.** A violated `CHECK` constraint restates an invariant the
   entity already guards, so it can only mean our code has a bug; reporting it as 409 would
   hide our defect inside the caller's error budget.
3. **No exception message reaches the client.** `detail` is `null` for domain 400s and all
   500s. Those messages carry internal parameter names and are formatted with the *server's*
   culture — on this host `-5.00` renders as `-5,00`.
4. **The handler decides severity, not EF Core.** The split is by **status**: a 4xx logs one
   `Warning` line with no exception object, a 5xx logs `Error` *with* the exception object, so the
   full inner chain and stack traces survive. Status and handled-versus-unhandled coincide everywhere
   except Ordering, whose dependency failure is classified as a 502 and therefore logs at `Error` —
   deliberate, since a dependency being down is worth paging on. It does mean the line reads
   "Unhandled exception" for a failure the handler did handle; see `KNOWN-ISSUES.md` → Observability.

**Log content.** Non-5xx lines walk the whole exception chain via `DescribeForLog`, capped at
depth 5 and joined with `->`. Both ends matter: for a binding failure the outer message names
the parameter that failed and only the inner one says why; for a database failure the reverse
is true. Logging only the outermost exception reports nothing useful, because EF's wrapper
says only "see the inner exception for details". `PostgresException` is formatted specially to
surface `SqlState`, `MessageText`, and `table=` / `column=` / `constraint=` when present;
`MessageText` is used rather than `Message`, which re-prefixes the same SQLSTATE.
`DbUpdateConcurrencyException` is the other special case, and for the opposite reason: its chain
walk finds nothing, because a zero-rows-affected `UPDATE` is not a SQL error and there is no inner
`PostgresException`, so EF's own ~230-character message plus documentation URL is replaced by one
concise line. It was 55 of 144 log lines in one Stock contention run.

Both EF Core failure categories are silenced in every service's `appsettings.json`:
`Microsoft.EntityFrameworkCore.Update`, and `Microsoft.EntityFrameworkCore.Database.Command`, the
sibling that logs a failed command at Error with the SQL text. The level is `"Fatal"` rather than
`"None"`, because Serilog's `LogEventLevel` has no `None` member and an unrecognised name is parsed as
a level switch, which throws while the host builds. The reasoning, and why no filter can distinguish a
handled failure from an unhandled one, are in `KNOWN-ISSUES.md` → Observability.

EF logs every `SaveChanges` failure at Error with a full stack trace before rethrowing and
cannot know whether it was handled, so a routine duplicate-key 409 used to emit roughly 40
lines. Severity classification belongs to the handler because only it knows whether the error
was handled.

Client cancellation returns `false`, handing the exception back to the framework: there is no
client left to receive a response, and logging it as an error would make every aborted request
look like an incident. The arm is guarded on `RequestAborted`, so a genuine internal timeout
still counts as a fault.

### 4.8 Correlation ID

`src/AgenticShop.Shared/Middleware/CorrelationIdMiddleware.cs`:

- Header name is the constant `CorrelationIdMiddleware.HeaderName` (`X-Correlation-Id`).
- An inbound value is adopted; otherwise `Guid.NewGuid().ToString("N")` — 32 lowercase hex.
- **The inbound value is untrusted.** It is echoed into a response header, embedded in the
  ProblemDetails body, assigned to `HttpContext.TraceIdentifier` and written to every log line
  for the request. It is accepted only if it is 1–128 characters (`MaxLength`) and contains
  nothing outside ASCII letters, digits and `-_.` — the characters that appear in GUIDs, W3C
  trace ids and conventional prefixed tokens.
- **The middleware also pushes it into Serilog's ambient context**, which is what makes "every log
  line" literally true: EF Core's entries and the request logger's completion event carry no id of
  their own and pick it up from there. Pushed inside this middleware rather than a second one so the
  scope necessarily wraps everything downstream — a push registered after the request logger would be
  popped before the completion event is written.
- An unacceptable value is **replaced**, not rejected: a malformed correlation id does not
  make the underlying request invalid, and refusing the call would turn a tracing concern into
  an availability one.
- Also stored in `HttpContext.Items[HeaderName]`; read back with the `GetCorrelationId()`
  extension, which falls back to `TraceIdentifier`.
- Registered first in the pipeline so later middleware failures still carry it.

Catalog and Stock are **inbound only**: neither makes an outbound call, and Stock's absence is
asserted by `LeafServiceBoundaryTests`. Ordering closes the loop with
`CorrelationIdPropagatingHandler`, a `DelegatingHandler` registered on both typed clients that
copies the ambient request's id onto every outbound call. It reads the id through
`IHttpContextAccessor` rather than taking it as a parameter, so no client method has to carry a
tracing concern in its signature, and it omits the header when there is no ambient request rather
than minting one — a trace that starts nowhere is worse than no trace.

Because every Ordering integration test replaces both clients at the seam, no automated test can
observe the header on the wire; it was verified live instead, and the evidence is recorded in
`src/AgenticShop.Ordering/docs/ARCHITECTURE.md` §6.

Phase 1 derives the id from `Activity.Current?.TraceId` instead, which is what makes the two
directions one concept rather than two — note that `Results.Problem` already adds a `traceId`
extension of its own, so a ProblemDetails body carries both today.

---

## 5. Testing architecture

Two projects per service — `<Service>.UnitTests` and `<Service>.IntegrationTests` — rather
than one shared test project, so fixtures do not multiply inside a single assembly.

| | Unit | Integration |
|---|---|---|
| Scope | domain rules, validation filter, error classification | real API over HTTP against real PostgreSQL |
| I/O | none | one Testcontainers container per collection |
| Docker | not needed | required |

Current counts, all passing:

| Project | Unit | Integration |
|---|---|---|
| Shared | 35 | — |
| Catalog | 64 | 39 |
| Stock | 77 | 61 |
| Ordering | 120 | 75 |
| **Total** | **296** | **175** |

**471 total.** Build is clean under `TreatWarningsAsErrors`.

The total did not change when the shared library was extracted: 35 tests moved out of
`AgenticShop.Catalog.UnitTests` into `AgenticShop.Shared.UnitTests`, because they specify the filter
and the correlation middleware, which are no longer Catalog's. `AgenticShop.Shared` has no
integration project — it has no host to start.

### 5.1 Why Testcontainers and never a mocked DbContext

Mocks pass while the real query fails. Concretely, only a real PostgreSQL could have surfaced:

- the duplicate-SKU `409` — needs the actual unique index to raise SQLSTATE `23505`
- the over-length-SKU `500` — needs a real `varchar(64)` to reject the insert
- the `xmin` concurrency token — a PostgreSQL system column with no in-memory equivalent
- the soft-delete query filter — real SQL translation

Stock adds four more that no in-memory provider could produce: the `UNIQUE(order_id,
stock_item_id)` duplicate-hold `409`, the `CHECK` constraints and their `23514` SQLSTATE, the
`RESTRICT` foreign key, and the enum-to-string conversion inside the `status IN (...)` check.

Ordering adds three that a mock could not even represent: materialising `Order.Lines` through a
readonly backing field (`PropertyAccessMode.Field`) on an `Include`, the `numeric(18,2)` precision
that `Order.MaxTotalAmount` is derived from, and a `varchar(30)` status column whose `CHECK` list
has to agree with a four-member enum.

Ordering's integration tests are also the only place the confirm-phase policy can be tested at all.
Both clients are faked there, which is normally a compromise and here is a requirement: "Stock
timed out after applying the confirm" cannot be produced deterministically against a live host
without putting a proxy in front of it. The database underneath is still real, so every assertion
about a persisted order status is a fact about PostgreSQL.

### 5.2 Harness design

Identical in shape for all three services, and deliberately duplicated rather than shared — the
three integration assemblies must not reference each other.

- `<Service>ApiFactory : WebApplicationFactory<Program>` calls `builder.UseEnvironment("Development")`
  **explicitly** rather than inheriting the framework default, because the suite depends on it:
  `Program.cs` runs `MigrateAsync()` only when `IsDevelopment()`. It also overrides
  `ConnectionStrings:<Service>` through `ConfigureAppConfiguration`, and in Ordering's case the two
  downstream base URLs as well — pointed at `.invalid`, a TLD RFC 2606 reserves so it cannot
  resolve, so a replacement that silently stops applying fails loudly instead of calling a service
  running on this machine.
- `<Service>ApiFixture : IAsyncLifetime` owns one `PostgreSqlContainer` built from that project's
  own `TestPostgreSql.Image`, exposed as `ICollectionFixture` via `<Service>ApiCollection` with
  `[Collection("<Service> API")]`. xunit runs a collection sequentially, which is what makes
  per-test `ResetDatabaseAsync()` safe.
- The per-test reset deletes every table, child before parent where a foreign key demands it:
  Catalog uses `IgnoreQueryFilters()` so soft-deleted rows go too, Stock deletes
  `stock_reservations` **before** `stock_items` (`RESTRICT`), and Ordering deletes `order_lines`
  before `orders` (`CASCADE`) and also resets its two client fakes. No fixture resets sequences —
  see `KNOWN-ISSUES.md`.
- `CreateScope()` exposes the host's services so a test can open independent units of work
  against the same database — which is what an optimistic-concurrency conflict needs.
- Disposal order is `Client` → factory → container. **Stock and Ordering guard this with
  `try/finally`; Catalog does not** — a known divergence recorded in `KNOWN-ISSUES.md`.
- Tests that assert on `ProblemDetails` deserialise the response body rather than depending on
  an internal result type.

### 5.3 Concurrency testing

**Anything with a mutable counter needs a parallel test, and a sequential one is not a
substitute.** This is the strongest testing lesson Stock produced.

In a verified 20-way parallel confirm burst against a single reservation, **all 20 requests
passed the state-machine check** — every one loaded the row while it was still `Pending` — and
only the `xmin` token stopped 19 of them. Across a full Stock run the split was **55 rejections
from the concurrency token versus 2 from the state machine**, and those 2 came from *sequential*
smoke checks. So `Confirm_Twice_Returns409AndDoesNotShipTwice` would still pass with the `xmin`
declaration deleted entirely.

The rules that follow:

- **Parallel assertions are inequalities, never exact counts.** Under contention the number of
  winners depends on interleaving, and Phase 0 does not retry server-side. A burst of 40 against
  10 units produced 4 successes, not 10. Assert what must hold every time: never oversold, no
  5xx, counters agree with committed rows, every request answered.
- **Cover the exact boundary in a separate sequential test.** Ten reserves succeed, the eleventh
  is 409 — deterministic, and it is the assertion the parallel test cannot make.
- **Assert against the database, not only the API.** After the burst, the pending reservation
  count and `SUM(quantity)` must equal `reserved`. That is the transaction-boundary check, and
  it is what proves the counter and the audit rows committed together.
- **Assert no 5xx under contention.** A misclassified `DbUpdateConcurrencyException` or an
  unmapped SQLSTATE would otherwise hide behind "the test passed".

Stock's `OversellingConcurrencyTests` holds all of these: the parallel burst, the sequential
boundary, the parallel confirm race, the parallel release race, a stale-writer rejection via two
independent scopes, first-writer-survives, sequential-writes-both-succeed, and the
counter-vs-rows agreement check.

Catalog has no equivalent, and deliberately so: `Product` has no contended numeric resource. Its
`ConcurrencyTests` cover the token at the `DbContext` level only.

### 5.4 Gotchas that have already bitten

- **The connection string must be resolved lazily inside the `AddDbContext` callback.**
  `WebApplicationFactory.ConfigureAppConfiguration` applies its override *during* `Build()`.
  Reading the value into a local beforehand freezes it, and the tests then run silently against
  the developer's own compose database while the Testcontainers container sits unused. This
  happened. `TestHostIsolationTests` now fails the suite if it recurs; it can also be proven by
  running the integration tests with `docker compose stop db`. All three services were verified
  this way — Stock's 61 and Ordering's 57 integration tests pass with port 5432 closed.
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
- **`GetCheckConstraints()` throws against EF's read-optimised runtime model.** Reading a CHECK
  constraint back in a test requires `db.GetService<IDesignTimeModel>().Model`, and the type
  lives in `Microsoft.EntityFrameworkCore.Metadata`, not `.Infrastructure`.
- FluentAssertions: `ThrowAsync<T>().And` (not `.Subject`) yields the exception;
  `NotContain(char)` has no overload, so use a string.
- `TestPostgreSql.Image` is each project's single declaration of the PostgreSQL image tag;
  `docker-compose.yml` states its own and all three `ComposeConfigurationTests` fail if they diverge.

### 5.5 Structural guards

A convention enforced only by prose will be violated. These fail the build or the suite:

| Guard | Protects | Present in |
|---|---|---|
| `Directory.Build.targets` | no cross-service `ProjectReference` (build time); exempts `AgenticShop.Shared` by name only | repo root |
| `RequestContractCoverageTests` | every `*Request` implements `IRequestContract`, and the check does not pass vacuously | all three |
| `ContractSchemaAlignmentTests` | schema and contracts agree — see below | all three, differently |
| `ComposeConfigurationTests` | compose image matches `TestPostgreSql.Image`; port is loopback-only; no literal credentials; isolation script mounted | all three |
| `TestHostIsolationTests` | the app under test really uses the Testcontainers database | all three |
| `LeafServiceBoundaryTests` | a service that makes no outbound calls registers no `HttpClient`, and references no `AgenticShop.*` assembly but the shared one | Stock |
| `ServiceBoundaryTests` | the orchestrator references no other service assembly, and *does* register its clients | Ordering |
| `scripts/verify-db-isolation.sh` | PostgreSQL role isolation, 22 checks | repo root |

`ContractSchemaAlignmentTests` reads limits from the EF model rather than restating them, so it
tracks the configuration instead of a second copy that could itself drift. Each service's version
covers the drift *that service* can suffer, and none is a copy of another:

| Service | What it compares |
|---|---|
| Catalog | DTO string limits against column limits — the guard that would have prevented the original over-length-SKU 500 |
| Stock | enum vocabulary against its `CHECK` list, status column width, quantity bounds against `StockItem.MaxQuantity` and an `integer` column, derived values not persisted, `xmin` on every entity |
| Ordering | the generated `OrderNumber` against its column, the summed `TotalAmount` against `numeric(18,2)`, and snapshot limits that mirror constants declared in two assemblies it must not reference |

Copying Catalog's version into Stock would have matched nothing and passed vacuously — the concrete
reason a structural guard must be re-derived per service rather than copied.

The extraction of `AgenticShop.Shared` re-proved the point from a new direction. Both
`RequestContractCoverageTests` and `ContractSchemaAlignmentTests` had been discovering a service's
DTOs through `typeof(IRequestContract).Assembly`; once the marker moved into the shared project that
expression named an assembly with no DTOs in it, and each guard would have scanned nothing and
passed. Both now anchor on one of their own contract types. What caught it was the
`TheCoverageCheckIsNotPassingVacuously` assertion, which is the argument for every reflection-based
guard here carrying one.
