# Decisions

**Global** choices: platform, topology, boundaries, the shared error contract, validation,
testing strategy, security, and what is deliberately not built yet. Why each was made and what
would reopen it.

Service-specific decisions live with the service and are not repeated here:

- `src/AgenticShop.Catalog/docs/DECISIONS.md` — soft delete, paging, money and currency
- `src/AgenticShop.Stock/docs/DECISIONS.md` — D1–D14 plus three taken during implementation
- `src/AgenticShop.Ordering/docs/DECISIONS.md` — O1–O16: the orchestrator's placement flow, the
  derived order number, the confirm-phase policy and the downstream client seam

Rules live in `AGENTS.md`; design detail in `ARCHITECTURE.md`; open problems in
`KNOWN-ISSUES.md`.

Entries are grouped by theme, not ordered by time. "Revisit" names the trigger, so a decision
is changed deliberately rather than drifted away from.

---

## Platform and toolchain

### Target `net10.0`, not `net11.0`

**Context.** The original brief specified .NET 11 / ASP.NET Core 11. As of September 2026
.NET 11 is still pre-release (GA November 2026) and no 11.x SDK is installed; the newest
present is 10.0.400.

**Decision.** Target `net10.0`, the current LTS. Raised with the project owner, who chose LTS
over installing a preview SDK.

**Why.** A preview SDK means pre-release EF Core packages, floating Docker tags and builds
that can break between sessions — poor trade for an educational project. .NET 10 is supported
to November 2028.

**Revisit.** After .NET 11 GA. It is a `TargetFramework` change in `Directory.Build.props`
plus package bumps; nothing in the codebase is 10-specific.

### `.slnx` rather than `.sln`

XML solution format, and the SDK 10 default. `.sln` embeds project GUIDs and is a
merge-conflict magnet when projects are added in parallel — which is the expected working mode
here. **Revisit:** never; there is no cost.

### `Directory.Build.targets` for the boundary guard, not an architecture test

**Context.** Nothing initially prevented a future `Ordering → Catalog` `ProjectReference`,
which would collapse the boundary into a distributed monolith.

**Decision.** An MSBuild target that fails the build, rather than an xunit test.

**Why.** It fails at build time instead of test time, applies to every current and future
service automatically with no test to remember to write, and needs no repo-root path walking
from a test assembly. Verified to fire using throwaway `ProbeA → ProbeB` projects, and
verified not to false-positive.

**Trade-off accepted.** It identifies services by the `/src/` path segment, so a layout change
silently disables it. An alternative would be a marker property in each service `.csproj`.

### `dotnet-tools.json` at the repository root

That is where `dotnet new tool-manifest` put it under SDK 10. Both `./dotnet-tools.json` and
`./.config/dotnet-tools.json` are valid manifest locations and `dotnet tool restore` finds
either. Moving it to `.config/` is marginally tidier but pure churn. **Revisit:** only if the
root becomes crowded.

### Central package management with transitive pinning

**Context.** `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3 transitively pulls EF Core
Relational **10.0.4** while `Microsoft.EntityFrameworkCore.Design` pulls **10.0.12**. The test
assemblies saw both and MSBuild raised `MSB3277`.

**Decision.** `ManagePackageVersionsCentrally` plus `CentralPackageTransitivePinningEnabled`,
with `Microsoft.EntityFrameworkCore` and `.Relational` declared at 10.0.12 purely to force the
transitive version. Neither is a direct `PackageReference`.

**Why this over the alternative.** Pinning `Design` down to 10.0.4 would remove the mismatch
but forfeit EF patches 10.0.5–10.0.12, some of which are security fixes. Running the Npgsql
provider against a newer EF patch level is supported — EF Core guarantees binary compatibility
within a major.

**Trade-off accepted.** `CentralPackageTransitivePinningEnabled` is repo-wide: from now on any
`PackageVersion` entry also overrides transitive versions of that package everywhere, so its
blast radius grows with the file.

**Revisit.** When a Npgsql provider release targets a newer Relational, the pinning can be
dropped. Also add `dotnet list package --vulnerable` once CI exists.

### Fixed ports

5081 / 5082 / 5083, set in each service's `launchSettings.json`. Chosen over dynamic ports so
cross-service base URLs are stable configuration rather than runtime discovery. **Revisit:**
if two developers must share one machine, which `container_name` also blocks.

---

## Topology and boundaries

### Three separate hosts, not a modular monolith

**Context.** The project owner was offered a modular monolith (one host, three modules), three
hosts, or a split-ready-contracts compromise, and chose three separate hosts.

**Why.** Real network hops from day one, so the failure modes the later phases teach actually
exist. The cost is service discovery, HTTP failure handling and three times the boilerplate —
accepted deliberately.

**Revisit:** never for this project; the point is the distribution.

### No shared library — extraction deferred to Phase 1

**Context.** With three services, a shared kernel is the obvious move. Built-in `ProblemDetails`
is contract enough for the error shape, so no shared assembly is *needed*.

**Decision (2026-09-24).** None in Phase 0. The filter, the exception-handler skeleton and the
correlation middleware are copied per service — Ordering carries the third copy — and extraction
is a committed Phase 1 item, to be done once alongside Serilog, OpenTelemetry, resilience
registration and health-check wiring.

**Why.** A shared assembly created on day one becomes a coupling magnet and a version-lock across
independently deployable services. Extracting now would also freeze the handler's shape before
Phase 1 changes it: Serilog and OpenTelemetry both touch the logging arms, and resilience policies
touch how a downstream 409 is classified. Designing the shared type twice costs more than copying
it a third time — and Ordering proved the point by needing two things the other services do not, a
502 bucket and an `orderId` extension on failures. Phase 1 therefore designs against four real
consumers rather than three near-identical ones.

**Cost accepted.** Three near-identical copies of roughly 500 lines each, so a fix to the skeleton
must be applied three times; the risk is silent divergence rather than breakage. Two mitigations:
the four invariants in `ARCHITECTURE.md` §4.7 are test-guarded in every service, and `AGENTS.md` §10
requires a convention proven wrong in a later service to be fixed in the earlier ones too.

**Must never be shared, in any phase:** the per-service structural guards (`TestPostgreSql.Image`,
`ComposeConfigurationTests`, `TestHostIsolationTests`, `ContractSchemaAlignmentTests`). Sharing
them would require the integration assemblies to reference each other, which is the coupling the
whole design avoids — see "Structural guards are re-derived per service" below.

**Do not re-raise extraction during Phase 0.** It is planned work with an owner phase.

### One PostgreSQL container hosting three databases

**Context.** The alternatives were three containers, or one database with three schemas.

**Decision.** One container, three databases, one per service.

**Why.** Three containers cost three volumes, three ports and three healthchecks to buy
isolation that roles provide anyway. One database with three schemas makes accidental coupling
structurally possible. Migration to separate instances later is a `pg_dump`/`pg_restore` per
database.

**Trade-offs accepted.** Shared CPU, IO and `max_connections`; one backup unit rather than
three. Both irrelevant at this scale and both disappear in Phase 4.

**Important correction.** An earlier draft of the documentation claimed cross-service access
was impossible "even by accident". That was false and was caught in review: the bootstrap role
was a superuser that connected to any database. The claim is now true because of per-service
roles, and `verify-db-isolation.sh` proves it. **A boundary enforced only by a comment is not
a boundary.**

### Per-service roles via ownership, not grants

**Decision.** Each role *owns* its database; `REVOKE ALL ON DATABASE ... FROM PUBLIC` removes
the default public `CONNECT`.

**Why ownership.** Since PostgreSQL 15 the `public` schema is owned by `pg_database_owner`, so
the owning role can create tables — which EF Core migrations require — with no `GRANT` and no
`ALTER DEFAULT PRIVILEGES` plumbing. Fewer moving parts, and privileges cannot drift as new
tables appear.

### PostgreSQL published on loopback only

`127.0.0.1:${POSTGRES_PORT:-5432}:5432`. Binding `0.0.0.0` exposed a superuser login, with a
password published in a committed file, to every host on the local network. Found in review.
`ComposeConfigurationTests` now fails if the binding regresses.

---

## Data

### `xmin` as the concurrency token, declared as a `uint` shadow property

**Context.** `UseXminAsConcurrencyToken()` is the documented Npgsql approach and **does not exist
in Npgsql 10** — verified against the assembly binary, not just the XML docs.

**Decision.** Declare it manually instead:

```csharp
builder.Property<uint>("xmin").IsConcurrencyToken().ValueGeneratedOnAddOrUpdate();
```

The mechanism that makes this work — and the proof that it emits no DDL — is in `ARCHITECTURE.md`
§4.3.

**Why concurrency tokens at all.** Without one, two overlapping writes both succeed and the second
silently discards the first. On `Product` that is a lost price update; on `StockItem` it is
overselling. The stakes differ per service, which is why the testing burden does too.

### Unique indexes named through a `public const`

Each service's exception handler branches on the constraint name to produce an accurate 409
message, so leaving the name to EF's convention would let a rename silently make every unique
violation report the wrong cause.

The fallback arm matters as much as the named ones: a unique index added without a matching case
reports "The value conflicts with an existing record" rather than misattributing the conflict.
Every handler is tested for that.

---

## API and error contract

### Minimal APIs, not controllers

Less ceremony, and one endpoint group per resource maps cleanly onto the eventual service
split. **Consequence that matters:** Minimal APIs do **not** validate body DTOs automatically —
that is MVC `[ApiController]` behaviour — hence `DataAnnotationValidationFilter`.

### No repository, service or `Application` layer

Handlers call the `DbContext` directly. At this size an extra layer is indirection without
benefit. **Revisit:** if a use case needs to coordinate two aggregates or an external call
inside one transaction.

**The revisit fired once.** Ordering's `OrderPlacer` is the collaborator this entry reserved room
for — no interface, one caller, one public method, in `Endpoints/` beside its caller. That is the
whole of the concession: `Application/` is still not a folder that exists, and a second
collaborator must be argued for on its own merits rather than inherited from this one. Detail in
`src/AgenticShop.Ordering/docs/DECISIONS.md` → O1.

### `BadHttpRequestException` answered with its own `StatusCode`

**Context.** Found in review: malformed JSON, an empty body, a non-numeric body field and a
non-numeric query parameter all returned **500**. All four arrive as `BadHttpRequestException`,
which carries `StatusCode = 400`, and the handler fell through to the catch-all.

**Decision.** A switch arm reading `badRequest.StatusCode`, not a hardcoded 400.

**Why not hardcode 400.** An oversized body is a 413; hardcoding would reintroduce the same
class of bug. Also note these failures occur during **binding**, before the validation filter
runs, so no filter can catch them — and they include query and route parameters, which have no
DTO at all.

### Client cancellation is not an error

`OperationCanceledException` with `RequestAborted` set logs at `Debug` and returns `false`,
handing it back to the framework. Otherwise every aborted request becomes a false-alarm Error
entry. The arm is guarded on `RequestAborted` so a genuine internal timeout still counts as a
fault.

### No exception message ever reaches the client

`detail` is `null` for domain 400s and all 500s. Those messages carry internal parameter names
and are formatted with the server's culture — observed live as `-5,00` on this Turkish-locale
host. They are logged, where the correlation id ties them to the response.

### The handler decides severity, and `Microsoft.EntityFrameworkCore.Update` is silenced

**Context.** EF logs every `SaveChanges` failure at Error with a full stack trace before
rethrowing, and cannot know whether it was handled. A routine duplicate-key 409 emitted roughly
40 lines; a genuine failure emitted two Error entries.

**Decision.** Set the category to `"None"` in `appsettings.json` (not the Development file —
the reasoning is environment-independent).

**Why this is not a loss.** The handler logs unhandled failures at Error *with* the exception
object, so the inner chain and stack traces survive. Only the component that knows whether an
error was handled should classify its severity.

**Rejected alternative.** A custom `ILoggerProvider` filtering by exception type. That is real
machinery, and Phase 1's Serilog work replaces it.

### Log the whole exception chain for handled rejections

**Context.** After the client-leak fix, the non-5xx log carried `exception.Message` — for a
`DbUpdateException`, EF's wrapper saying only "see the inner exception for details". The
useful `SqlState` and `ConstraintName` were in the inner exception and unlogged.

**Decision.** `DescribeForLog` walks the chain outermost-first, capped at depth 5, joined with
`->`. `PostgresException` gets dedicated formatting.

**Why the whole chain and not just the root.** Both ends carry different information: for a
binding failure the outer message names the parameter and only the inner one says why; for a
database failure the reverse is true.

### Correlation id replaced, not rejected

An over-long or malformed inbound `X-Correlation-Id` is discarded in favour of a freshly minted
id. Refusing the request would turn a tracing concern into an availability one, and the value is
untrusted input that ends up in a response header, the ProblemDetails body and every log line.
Accepted charset is ASCII letters, digits and `-_.`, max 128 characters — enough for a GUID, a
W3C trace id or a prefixed token.

### OpenAPI and Scalar are Development-only

A publicly reachable Scalar UI is an interactive attack surface.

### `RequireConnectionString` resolved lazily, validated eagerly

**Context.** A first attempt read `GetConnectionString("Catalog")` into a local before
`Build()`. That froze the value before `WebApplicationFactory.ConfigureAppConfiguration` could
override it, so **the integration tests silently ran against the developer's own compose
database** while the Testcontainers container sat unused. Found because an E2E check saw a stray
row.

**Decision.** Resolve inside the `AddDbContext` callback (lazy, so overrides apply) and call
`RequireConnectionString(app.Configuration)` after `Build()` (eager, so a missing value fails
at startup rather than on the first request).

**Revisit:** never. `TestHostIsolationTests` guards it.

---

## Validation

### An endpoint filter, not FluentValidation

**Context.** Minimal APIs do not validate body DTOs. Without something, `Contracts/` attributes
are decorative and an over-length SKU reached PostgreSQL, which rejected it and surfaced as a
500.

**Decision.** A ~185-line `IEndpointFilter` using `System.ComponentModel.DataAnnotations`. No
new dependency.

**Rejected alternatives.** FluentValidation (new dependency, more ceremony); moving length rules
into the domain (duplicates the EF column configuration); controllers (loses Minimal APIs).

### `IRequestContract` marker, not namespace detection

**Context.** The first implementation detected DTOs by namespace. A DTO in any other folder was
silently not validated — the same silent-failure class as the bug being fixed.

**Decision.** An explicit marker interface, plus `RequestContractCoverageTests` which reflects
over `Contracts/` and fails the build if any `*Request` type omits it. That test also asserts it
matches at least two types, so it cannot pass vacuously if the naming convention changes.

### Recursion, because `Validator.TryValidateObject` does not cascade

**Context.** Catalog has no nested DTO, so nothing broke. But Ordering's `CreateOrderRequest`
will carry a `Lines` collection, and copying a non-cascading filter would validate the top level
and silently skip every line.

**Decision.** The filter recurses into nested objects and collections, reporting full member
paths. A second bug was found while testing it: a collection must be traversed **by element**,
because reflecting over a `List<T>` yields `Count` and `Capacity`, so property-only recursion
validated nothing below the first level.

---

## Observability

### Built-in logging, no Serilog yet

Sufficient for one service, and its configuration is about to change anyway. **Revisit:**
Phase 1.

### Correlation id, not OpenTelemetry

A correlation id is 60 lines and immediately useful. Distributed tracing had nothing to trace until
a second service existed; one now makes an outbound call and the id does cross the hop. What it
still cannot answer is *which call was slow*, because there is no span tree — the gap Phase 1 closes.

**Consequence to handle in Phase 1:** `Guid.NewGuid().ToString("N")` is not W3C
`traceparent`-compatible, so the id should be derived from `Activity.Current?.TraceId` when
OpenTelemetry arrives, or there will be two parallel correlation concepts. A hint of that already
shows up in responses: `Results.Problem` adds a `traceId` extension from the ambient activity, so a
ProblemDetails body carries both ids today and only one of them is ours.

---

## Testing

### Testcontainers, never a mocked `DbContext`

See `ARCHITECTURE.md` §5.1 for the concrete cases only a real PostgreSQL could surface — four from
Catalog, four more from Stock, three more from Ordering. This decision has paid for itself
repeatedly.

### Per-service test projects, not one shared suite

So fixtures do not multiply inside a single assembly. **Trade-off accepted:** each integration
test project spins its own container, so a full `dotnet test` with three services runs three
PostgreSQL containers concurrently.

### Structural guards as tests

Conventions enforced only by prose get violated. Guards exist for the marker interface, DTO-to-column
limit alignment, compose image drift, loopback binding, literal credentials, test-host isolation
and the service boundary. **Trade-off accepted:** `ComposeConfigurationTests` matches compose
text by substring, so reformatting `docker-compose.yml` can break it — chosen over taking a YAML
parser dependency.

### `FrameworkReference` in the unit test project

Needed to unit-test `IEndpointFilter` and `IExceptionHandler`, which are ASP.NET Core types. It
makes the "unit" project depend on the ASP.NET Core shared framework, which is acceptable: these
are components of a web service, and the tests still perform no I/O.

### Parallel concurrency tests are mandatory for any mutable counter

**Context.** Stock's sequential state-machine tests looked thorough and proved almost nothing about
concurrency — the measurements are in `ARCHITECTURE.md` §5.3, and they are unflattering.

**Decision.** Any entity with a mutable counter gets a parallel test. Assertions are
**inequalities** (never oversold, no 5xx, counters agree with committed rows, every request
answered), the exact boundary gets its own *sequential* test, and results are asserted against the
database rather than only the API.

**Why.** `Confirm_Twice_Returns409AndDoesNotShipTwice` passes with the `xmin` declaration deleted.
A test that cannot fail when the safety mechanism is removed is not testing the mechanism.

**Rejected.** Exact-count assertions — flaky, and they test the scheduler rather than the design.
One such assertion did ship, and did hide a server fault behind a tally mismatch; see
`KNOWN-ISSUES.md` → Resolved.

### Structural guards are re-derived per service, not copied

**Decision.** Each service writes guards for the drift *it* can suffer. The three
`ContractSchemaAlignmentTests` share nothing but a name — what each covers is tabulated in
`ARCHITECTURE.md` §5.5.

**Why.** A verbatim copy of Catalog's into Stock would have matched zero properties and passed
vacuously — the exact failure mode its own non-vacuity assertion exists to catch.

**Consequence accepted.** More duplicated guard scaffolding. It is the price of the guards being
worth anything, and it is why the guards are excluded from the Phase 1 shared-library extraction.

---

## Security and configuration

### Published placeholder credentials

`.env.example` and `appsettings.Development.json` contain literal local-only passwords
(`local_only_bootstrap`, `local_only_catalog`, `local_only_stock`, `local_only_ordering`).

**Why this is acceptable.** The container is loopback-bound, applications use least-privilege
roles, the data is disposable, and the values are named to be unmistakable. Every occurrence is
commented as local-only. Generating random credentials per clone would add friction without
protecting anything real.

**Rejected.** A secret-management system — there is no deployment to manage secrets for.

**Guards.** `ComposeConfigurationTests` fails on a literal credential in the compose file;
startup fails if a connection string is absent, so no non-Development config file needs one;
`UserSecretsId` is declared for anyone who wants different local values.

### Fail fast on missing configuration

A service that boots, reports healthy, and then 500s on every request is worse than one that
refuses to start. See the lazy/eager resolution decision above.

### `launchSettings.json` with a single `http` profile

The generated `https` profile was dropped: the services talk plain HTTP locally and dev certs
only add friction. Fixed port 5081 is worth more than local TLS at Phase 0. **Known trap,
documented rather than fixed:** `dotnet run` always applies `launchSettings.json`, whose profile
pins `ASPNETCORE_ENVIRONMENT=Development` and overrides the command line. Use
`--no-launch-profile`.

---

## Deliberately not done

Each was considered and declined, with the phase that would justify it:

| Declined | Why not yet |
|---|---|
| RabbitMQ / any broker | Nothing is asynchronous |
| Outbox / Inbox | An outbox makes "write a row and publish an event" atomic; there are no events. Note the design is already outbox-ready: every write path is a single `SaveChangesAsync`, so an entity and an outbox row would already be atomic. Stock's reserve commits the counter and the reservation row together, which is the same property |
| Redis | No cache pressure, no distributed idempotency store to hold |
| Polly / resilience | Retrying without idempotency keys double-reserves stock. Also see D9: Stock deliberately returns 409 on `xmin` conflict rather than retrying |
| OpenTelemetry | A correlation id crosses the hop and appears in both logs, which is enough for three services and one synchronous path. A span tree answers "which call was slow", and nothing is slow yet |
| Serilog | Built-in logging suffices and the configuration is about to change |
| Shared infrastructure library | Deferred to Phase 1 by decision (2026-09-24), after the trigger fired — see "No shared library" above |
| Authentication / authorization | Deferred by the project owner. Adding it later needs a customer identity column on `Order` plus a cross-cutting policy — a migration and a concern, which is why no placeholder seam was pre-built |
| Kubernetes / Helm, YARP gateway | Phase 4 |
| CQRS, DDD tactical patterns, event sourcing | No read pressure, no aggregate boundaries to enforce |
| Idempotency keys | Phase 1. Two operations are already naturally idempotent through unique constraints: `POST /products` (unique SKU) and `POST /stock/{id}/reservations` (unique order + stock item). **`POST /orders` is not** — a retried placement creates a second order and a second set of holds, which for an order that actually confirmed means shipping twice. That is now the largest open residual in the repository, and it is why keys precede retry policies rather than following them |
| Reservation expiry / `ExpiresAtUtc` | Phase 3. Release is an explicit call only; no background worker. The cost became concrete with Ordering: a release that fails during compensation strands the hold, and nothing lifts it |
| Payment / any money movement | Not in scope at any phase. It is also what keeps the confirm-phase residual survivable — a `PartiallyConfirmed` order strands *inventory*, not money. Adding a payment step would turn it into a financial inconsistency and would need the outbox and the saga first |
| Compensating return-to-stock for a confirmed reservation | Stock's `Confirmed` is terminal by decision D3 and confirm decrements `quantity_on_hand`, so undoing one needs a new inventory-adjustment transition with its own audit trail. Ordering records a terminal `PartiallyConfirmed` order instead — see `src/AgenticShop.Ordering/docs/DECISIONS.md` → O14 |
| Service Dockerfiles | Phase 0 runs APIs on the host for a fast inner loop |
| CI pipeline | Phase 1 |
| CORS policy | No browser client |
| Rate limiting, HTTPS, HSTS, tightened `AllowedHosts` | No non-local deployment |
| Stock list/paging endpoint | Nothing needed it; symmetry is not a reason |
