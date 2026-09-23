# Known issues — global

Issues that affect the repository, the build, the shared infrastructure, or more than one service.
Service-specific items live in `src/<Service>/docs/KNOWN-ISSUES.md`.

Open items are accepted residuals with a named owner phase — not a backlog to clear
opportunistically. Resolved items are kept because *what caught them* is the instructive part.

Severity: **High** would mislead or harm if replicated · **Medium** a correctness or
observability gap · **Low** hygiene · **Info** a trade-off worth knowing.

---

## Open

### Observability

**`Microsoft.EntityFrameworkCore.Database.Command[20102]` logs at Error for a handled 409.**
*Medium · Phase 1.* `Microsoft.EntityFrameworkCore.Update` is silenced in both services, but this
separate category still emits four lines including the failing SQL for a routine duplicate key.
Category-level filtering cannot distinguish handled from unhandled failures — only a filter
inspecting the exception can. Silencing this category too would cost the SQL text for genuine
failures, which is the single most useful thing when diagnosing an EF translation bug.
**Decide with Serilog in Phase 1**, not before.

*Narrower than first thought.* In Stock's full verification run this category fired **once** — on
the deliberate duplicate reserve. The 55 `xmin` conflicts produced no SQL-level error at all,
because a zero-rows-affected `UPDATE` is not a SQL failure. So this affects unique and CHECK
violations, not concurrency conflicts, which lowers its priority.

**Validation rejections are not logged at all.** *Medium · Phase 1.*
`DataAnnotationValidationFilter` returns a result rather than throwing, so it never reaches the
handler. In a verified Catalog smoke run, 8 client-error responses produced 5 handler warnings —
the 3 validation ones were silent. "Clients are sending invalid data" is therefore invisible.
Logging every malformed request would be noise, so this wants metrics or sampled request logging
rather than a log line.

**`DbUpdateConcurrencyException` logs the least useful line under contention.** *Low · Phase 1.*
Each warning carries EF's full boilerplate including a documentation URL — roughly 230 characters.
In one verified Stock run, **55 of 144 log lines** were this message and the URL appeared 55 times.

`DescribeForLog` is not at fault: it walked the chain correctly and found nothing to walk to,
because a zero-rows-affected `UPDATE` is not a SQL error and there is no inner `PostgresException`.
Contrast the 2 unique violations, which logged `PostgresException 23505: duplicate key ...
(table=stock_reservations, constraint=ix_stock_reservations_order_id_stock_item_id)` — genuinely
diagnostic.

A special case in `Describe` would reduce it to something like
`DbUpdateConcurrencyException: 0 rows affected (concurrency token mismatch)`. Not done, because the
fix belongs in **both** handlers — raising it rather than folding it in. Good candidate to batch
with the shared-library decision, which now stands at 2 of 3.

### Testing

**Sequential state-machine tests do not prove concurrency safety.** *Info, but load-bearing.*
Verified from Stock's logs: in a 20-way parallel confirm burst all 20 requests passed the
state-machine check, and only `xmin` stopped 19. Across the run, 55 rejections came from the
concurrency token and 2 from the state machine. `Confirm_Twice_Returns409AndDoesNotShipTwice`
would still pass with the token deleted. Any new counter needs a parallel test.

**`ResetDatabaseAsync` truncates rows but does not reset sequences.** *Low.* Irrelevant today —
neither service has an identity or sequence column. It will matter the moment `OrderNumber` uses
one, at which point tests would see ever-increasing values and any assertion on them becomes
order-dependent. Applies to both fixtures.

**No Testcontainers reuse.** *Low.* Each `dotnet test` starts a fresh container (~3–5s). Two
services now means **two** PostgreSQL containers concurrently; three will mean three. Worth knowing
before CI.

**`coverlet.collector` is referenced but nothing consumes it.** *Low.* In all four test projects.
No coverage report, threshold or CI step. Either wire up `--collect:"XPlat Code Coverage"` or drop
the reference.

**`CatalogApiFixture.DisposeAsync` is not exception-safe.** *Low.* If `_factory.DisposeAsync()`
throws, `_postgres.DisposeAsync()` never runs and the container leaks until Ryuk reaps it. Wants a
`try/finally`. **`StockApiFixture` already has one** — fixed forward rather than copied. Recorded
here rather than in a service file because it is a divergence *between* the two services, and
`AGENTS.md` §10 says a later service's improvement should be carried back to the earlier one.

**Structural guards are duplicated per service, by design.** *Info.* `TestPostgreSql.Image`,
`ComposeConfigurationTests`, `TestHostIsolationTests` and `RequestContractCoverageTests` exist in
both test suites, and `ContractSchemaAlignmentTests` exists in both but with completely different
content — Stock's had to be rewritten because Catalog's version would have matched nothing and
passed vacuously. Sharing them would require the test assemblies to reference each other, which is
the coupling the design avoids.

### Data and API

**Unknown JSON fields are silently ignored.** *Info.* Verified in Catalog: `{"bogus":1}` returns
201. Framework behaviour, so it applies to every service. Good for forward compatibility, but a
client typo like `"prices": 5` is accepted and silently falls back to the default.

### Infrastructure

**`Directory.Build.targets` identifies services by the `/src/` path segment.** *Medium.*
Restructuring the repository silently disables the boundary guard. An alternative is a marker
property in each service `.csproj`, or a guard test asserting the target still exists.

**`ComposeConfigurationTests` matches compose text by substring.** *Low.* Reformatting
`docker-compose.yml` can break it. Chosen over taking a YAML parser dependency.

**`container_name: agenticshop-db` is hardcoded.** *Low.* Prevents running two stacks on one
machine — a second worktree, or a CI job alongside local development.

**`global.json` pins `10.0.400` with `rollForward: latestFeature`.** *Low.* Rejects a machine
that only has `10.0.1xx`. Deliberate for reproducibility, but it will surprise a new contributor.

**`CentralPackageTransitivePinningEnabled` is repo-wide.** *Info.* Any future `PackageVersion`
entry also overrides transitive versions of that package everywhere, so adding a direct reference
can silently shift a transitive dependency elsewhere. Contained today because only EF Core packages
are declared.

**PostgreSQL image tag is declared in three places.** *Info.* `docker-compose.yml` plus one
`TestPostgreSql.Image` per integration project. Both `ComposeConfigurationTests` fail if they
diverge, so drift is caught rather than silent — but it is three places to edit, and it becomes
four with Ordering.

**No CORS policy.** *Info.* Irrelevant for service-to-service; will block a browser admin UI.

**`AllowedHosts: "*"`, no rate limiting, no HTTPS/HSTS.** *Info.* Correct for local Phase 0.
Tighten before any non-local deployment.

**`UserSecretsId` is declared but unused.** *Info.* Both services declare one;
`appsettings.Development.json` holds the local placeholder instead. It is documented in
`README.md` as the mechanism for anyone wanting different local values, which makes it meaningful
rather than decorative — but nothing enforces that.

**FluentAssertions 8.x licensing.** *Info.* Free for open-source, personal and educational use;
licensed above a revenue threshold commercially. Relevant if this becomes a portfolio piece
attached to commercial work. `Shouldly` (Apache-2.0) is the drop-in alternative.

### Deliberate, not defects

**Cross-cutting code is duplicated across services — counter now 2 of 3.** The filter, exception
handler and correlation middleware are copied into Stock (~517 lines). See the trigger in
`DECISIONS.md`: **building Ordering fires it**, not Phase 1, because the error handler's
chain-describing logic is already entirely generic.

**Catalog-only conventions are not house style.** Soft delete, query filters and offset paging
belong to Catalog. Stock has none of them, and an order must never vanish from a list either.
Details in `src/AgenticShop.Catalog/docs/DECISIONS.md`.

---

## Resolved

Kept for the lesson, not the fix. These concern shared infrastructure or the repository itself;
service-specific findings are recorded with the service.

### Minimal APIs do not validate body DTOs → HTTP 500

An 84-character SKU returned **500**. DataAnnotations never ran — automatic model validation is
an MVC `[ApiController]` behaviour — so the value reached PostgreSQL, which rejected it with
`22001 value too long for type character varying(64)`, and the handler mapped it to the
catch-all. Fixed by `DataAnnotationValidationFilter`, regression-tested.
**Caught by:** manual probing during review, after the suite was already reporting green.

### Four binding failures returned HTTP 500

Malformed JSON, an empty body, a non-numeric body field and a non-numeric **query** parameter all
returned 500. All four arrive as `BadHttpRequestException`, which carries `StatusCode = 400`, and
the handler had no arm for it. The validation filter cannot help: these fail during binding,
before the filter runs, and the query-parameter case has no DTO at all. Fixed by reading
`badRequest.StatusCode`. **Caught by:** systematic probing of every error path.

### Nested collections were never validated

The filter recursed by property. Reflecting over a `List<T>` yields `Count` and `Capacity`, so a
collection nested inside a collection was silently skipped. Invisible in Catalog, which has no
nested DTO; it would have broken Ordering's `CreateOrderRequest.Lines`. Fixed by traversing any
`IEnumerable` by element. **Caught by:** designing a test case for two collection levels.

### Integration tests ran against the developer's own database

`Program.cs` read `GetConnectionString("Catalog")` into a local before `Build()`.
`WebApplicationFactory.ConfigureAppConfiguration` applies its override *during* `Build()`, so the
Testcontainers connection string never reached the `DbContext`. **25 tests passed while
truncating and writing the real local database**, and the container sat unused. Fixed by
resolving lazily and validating after `Build()`; `TestHostIsolationTests` now guards it in both
services. **Caught by:** an E2E smoke check finding a stray row — not by the test suite.

### The service boundary was asserted, not enforced

Documentation claimed cross-service database access was impossible "even by accident". The
bootstrap role was a **superuser** (`rolsuper = t`) that connected to any database without
complaint. Fixed with per-service non-superuser roles, ownership, and
`REVOKE ALL ... FROM PUBLIC`; `verify-db-isolation.sh` proves it with 22 checks.
**Caught by:** running the claim instead of reading it.

### PostgreSQL exposed on all interfaces

`0.0.0.0:5432` with a superuser and a password published in a committed file — reachable from
anything on the local network. Fixed by binding `127.0.0.1`, now guarded by both
`ComposeConfigurationTests`. **Caught by:** `docker port` during review.

### Internal exception messages leaked to clients

`ArgumentException.Message` was returned as `ProblemDetails.detail`, observed live as
`price ('-5,00') must be a non-negative value. (Parameter 'price')`. Two problems: internal
parameter names disclosed, and the value formatted with the *server's* culture. Fixed by
returning `null` and logging instead. **Caught by:** reading an actual response body.

### Handled rejections logged the wrong thing

After the leak fix, the non-5xx log carried `exception.Message` — for a `DbUpdateException`, EF's
wrapper saying only "see the inner exception for details". The useful `SqlState` and
`ConstraintName` were unlogged, and the template produced a doubled period. Fixed by walking the
chain and dropping the appended punctuation. **Caught by:** reading a real log line.

### A handled 409 emitted ~40 lines of Error-level stack trace

`Microsoft.EntityFrameworkCore.Update` logs every `SaveChanges` failure at Error before
rethrowing and cannot know whether it was handled, which defeated the `warn`-vs-`fail`
distinction the handler exists to make. Fixed by silencing the category in both services; the
handler still logs genuine failures at Error with the exception attached.
**Caught by:** a task-notification log tail.

### `UseXminAsConcurrencyToken()` does not exist in Npgsql 10

The documented approach produced a compile error. Verified absent from the assembly binary, not
just the XML docs. Npgsql 10 instead auto-maps a `uint` concurrency token generated
`OnAddOrUpdate` via `NpgsqlPostgresModelFinalizingConvention`. **Caught by:** the compiler.

### Smaller compile-time findings

`UseSnakeCaseNamingConvention()` hangs off `DbContextOptionsBuilder`, not the Npgsql sub-builder.
`HttpResponseMessage` has no `JsonContentAs<T>()` — it is `Content.ReadFromJsonAsync<T>()`.
`HttpValidationProblemDetails` is in `Microsoft.AspNetCore.Http`, while `ProblemDetails` is in
`Microsoft.AspNetCore.Mvc`. `PostgreSqlBuilder()` parameterless is obsolete in Testcontainers 4.x
and, with `TreatWarningsAsErrors`, a build error. `ValidationProblemHttpResult` is not accessible
from a test assembly. `PostgresException` fields are read-only. All caught by the compiler.

### `dotnet run` silently overrides the environment

`launchSettings.json` pins `ASPNETCORE_ENVIRONMENT=Development` and wins over a value set on the
command line. A smoke test intended to run without migrations ran them instead and crashed on an
unreachable database. Documented rather than fixed; use `--no-launch-profile`.
**Caught by:** a startup crash.
