# Known issues

Open items are accepted residuals with a named owner phase — not a backlog to clear
opportunistically. Resolved items are kept because *what caught them* is the instructive part.

Severity: **High** would mislead or harm if replicated · **Medium** a correctness or
observability gap · **Low** hygiene · **Info** a trade-off worth knowing.

---

## Open

### Observability

**`Microsoft.EntityFrameworkCore.Database.Command[20102]` logs at Error for a handled 409.**
*Medium · Phase 1.* `Microsoft.EntityFrameworkCore.Update` is silenced, but this separate
category still emits four lines including the failing SQL for a routine duplicate key.
Category-level filtering cannot distinguish handled from unhandled failures — only a filter
inspecting the exception can. Silencing this category too would cost the SQL text for genuine
failures, which is the single most useful thing when diagnosing an EF translation bug.
**Decide with Serilog in Phase 1**, not before.

**Validation rejections are not logged at all.** *Medium · Phase 1.*
`DataAnnotationValidationFilter` returns a result rather than throwing, so it never reaches the
handler. In a verified smoke run, 8 client-error responses produced 5 handler warnings — the 3
validation ones were silent. "Clients are sending invalid data" is therefore invisible. Logging
every malformed request would be noise, so this wants metrics or sampled request logging rather
than a log line.

### Testing

**No HTTP-level optimistic-concurrency test.** *Low.* The `xmin` token is proven at the
`DbContext` level (`ConcurrencyTests`, two independent units of work) and the 409 mapping is
proven in `CatalogExceptionHandlerTests`. It cannot be provoked deterministically over HTTP:
each request loads the row fresh, so it always carries the current `xmin`. Only two genuinely
overlapping requests collide, and a test that races would be flaky. Deliberate.

**`ResetDatabaseAsync` truncates rows but does not reset sequences.** *Low.* Irrelevant today —
Catalog has no identity or sequence column. It will matter the moment `OrderNumber` uses one, at
which point tests would see ever-increasing values and any assertion on them becomes
order-dependent.

**No Testcontainers reuse.** *Low.* Each `dotnet test` starts a fresh container (~3–5s). With
three services, xunit's default assembly-level parallelism means three PostgreSQL containers
concurrently. Worth knowing before CI.

**`CatalogApiFixture.DisposeAsync` is not exception-safe.** *Low.* If `_factory.DisposeAsync()`
throws, `_postgres.DisposeAsync()` never runs and the container leaks until Ryuk reaps it.
Wants a `try/finally`.

**`coverlet.collector` is referenced but nothing consumes it.** *Low.* No coverage report,
threshold or CI step. Either wire up `--collect:"XPlat Code Coverage"` or drop the reference.

**Two structural test suites are Catalog-specific.** *Low.* `ContractSchemaAlignmentTests`
inspects `Product`; `RequestContractCoverageTests` scans Catalog's `Contracts/`. Both must be
recreated per service — they are not shared, by design.

**Minor test-debt items.** *Info.* `Deactivate()` bumps `UpdatedAtUtc` but nothing asserts it.
`Create_RejectsNegativePrice` casts `double`→`decimal` while the rounding theory two tests above
deliberately uses invariant-culture strings — the inconsistency invites an imprecise case later.

### Data and API

**Paging is offset-based and not atomic.** *Low.* `CountAsync` and `ToListAsync` are two queries
outside a transaction, so `TotalCount` can disagree with the page under concurrent writes.
Standard, and acceptable at this scale. Keyset paging is the eventual answer.

**Soft-deleted entities cannot be reactivated.** *Low.* `Update` does not accept `IsActive` and
no endpoint exposes it, so an accidental `DELETE` is only reversible with SQL. Possibly
intentional; never explicitly decided.

**Unknown JSON fields are silently ignored.** *Info.* Verified: `{"bogus":1}` returns 201. Good
for forward compatibility, but a client typo like `"prices": 5` is accepted and silently falls
back to the default.

**`Product.Create` assigns its own `Guid`.** *Info.* A caller cannot supply a deterministic id.
Fine for idempotency keyed on a client token, but worth knowing before designing
client-generated identities.

### Infrastructure

**`Directory.Build.targets` identifies services by the `/src/` path segment.** *Medium.*
Restructuring the repository silently disables the boundary guard. An alternative is a marker
property in each service `.csproj`, or a guard test asserting the target still exists.

**`ComposeConfigurationTests` matches compose text by substring.** *Low.* Reformatting
`docker-compose.yml` can break it. Chosen over taking a YAML parser dependency.

**`container_name: agenticshop-db` is hardcoded.** *Low.* Prevents running two stacks on one
machine — a second worktree, or a CI job alongside local development.

**`global.json` pins `10.0.400` with `rollForward: latestFeature`.** *Low.* Rejects a machine
that only has `10.0.1xx`. Deliberate for reproducibility, but it will surprise a new
contributor.

**`CentralPackageTransitivePinningEnabled` is repo-wide.** *Info.* Any future `PackageVersion`
entry also overrides transitive versions of that package everywhere, so adding a direct
reference can silently shift a transitive dependency elsewhere. Contained today because only
EF Core packages are declared.

**PostgreSQL image tag is declared twice.** *Info.* `docker-compose.yml` and
`TestPostgreSql.Image`. `ComposeConfigurationTests` fails if they diverge, so drift is caught
rather than silent — but it is still two places to edit.

**No CORS policy.** *Info.* Irrelevant for service-to-service; will block a browser admin UI.

**`AllowedHosts: "*"`, no rate limiting, no HTTPS/HSTS.** *Info.* Correct for local Phase 0.
Tighten before any non-local deployment.

**`UserSecretsId` is declared but unused.** *Info.* `appsettings.Development.json` holds the
local placeholder instead. It is documented in `README.md` as the mechanism for anyone wanting
different local values, which makes it meaningful rather than decorative — but nothing enforces
that.

**FluentAssertions 8.x licensing.** *Info.* Free for open-source, personal and educational use;
licensed above a revenue threshold commercially. Relevant if this becomes a portfolio piece
attached to commercial work. `Shouldly` (Apache-2.0) is the drop-in alternative.

### Deliberate, not defects

**Cross-cutting code is duplicated across services.** See the trigger in `DECISIONS.md` —
Phase 1, or the third copy, whichever comes first.

**`Currency` carries both `[StringLength(3,3)]` and `[RegularExpression]`.** Redundant on
length, intentional: one guards the column, the other the charset, and
`ContractSchemaAlignmentTests` depends on `StringLength` being present.

---

## Resolved

Kept for the lesson, not the fix.

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
resolving lazily and validating after `Build()`; `TestHostIsolationTests` now guards it.
**Caught by:** an E2E smoke check finding a stray row — not by the test suite.

### The service boundary was asserted, not enforced

Documentation claimed cross-service database access was impossible "even by accident". The
bootstrap role was a **superuser** (`rolsuper = t`) that connected to any database without
complaint. Fixed with per-service non-superuser roles, ownership, and
`REVOKE ALL ... FROM PUBLIC`; `verify-db-isolation.sh` proves it with 22 checks.
**Caught by:** running the claim instead of reading it.

### PostgreSQL exposed on all interfaces

`0.0.0.0:5432` with a superuser and a password published in a committed file — reachable from
anything on the local network. Fixed by binding `127.0.0.1`, now guarded by
`ComposeConfigurationTests`. **Caught by:** `docker port` during review.

### `HasDefaultValue(true)` on a non-nullable `bool`

EF's sentinel for `bool` is `false`, so an explicit `false` would be dropped from the INSERT and
the database default applied instead — a product created inactive would come back active.
Latent: unreachable while `Create` always set `true`, which is exactly why it was dangerous.
Fixed by removing the default. **Caught by:** reading the model snapshot.

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
distinction the handler exists to make. Fixed by silencing the category; the handler still logs
genuine failures at Error with the exception attached. **Caught by:** a task-notification log
tail.

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
