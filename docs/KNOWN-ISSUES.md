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
*Resolved in Catalog 2026-10-04 · still open for Stock and Ordering.* Catalog silences the category
with a `Fatal` level override; the other two still use the built-in providers and still emit it.

**The documented diagnosis was wrong, and the correction matters more than the fix.** This entry
claimed that "category-level filtering cannot distinguish handled from unhandled failures — only a
filter inspecting the exception can." It cannot. A Serilog `ILogEventFilter` was written to do
exactly that, its tests passed, and a live duplicate-SKU 409 still logged at Error — because
**EF Core attaches no exception to this event at all**. It logs from inside `RelationalCommand` and
rethrows separately, so the entry carries the SQL text and nothing else. Verified by reading the
real log line; the rendered `{Exception}` segment was empty.

Nothing else on the event distinguishes the cases either, and nothing could: EF writes the entry
before the handler has run, so whether the failure will be handled does not exist yet. The choice is
therefore binary — silence the category or keep a false Error on every duplicate key — and it was
silenced, extending the reasoning already accepted for `Microsoft.EntityFrameworkCore.Update`: only
the component that knows whether an error was handled should classify its severity.

**What that costs, stated plainly.** The literal SQL and parameter list for a genuine failure. Less
than the old text implied: the handler logs every failure at Error with the full exception chain, and
its `PostgresException` formatting already surfaces `SqlState` plus `table=`, `column=` and
`constraint=`. The old text also justified keeping the category by "diagnosing an EF translation
bug", but a translation failure throws before any command executes and is logged under
`Microsoft.EntityFrameworkCore.Query`, not here.

*Narrower than first thought.* In Stock's full verification run this category fired **once** — on
the deliberate duplicate reserve. The 55 `xmin` conflicts produced no SQL-level error at all,
because a zero-rows-affected `UPDATE` is not a SQL failure. So this affects unique and CHECK
violations, not concurrency conflicts, which lowers its priority for the two services still carrying
it.

**Validation rejections are not logged at all.** *Resolved in Catalog 2026-10-04 · still open for
Stock and Ordering.* `DataAnnotationValidationFilter` returns a result rather than throwing, so it
never reaches the handler; in a verified Catalog smoke run, 8 client-error responses produced 5
handler warnings and the 3 validation ones were silent. Catalog now runs Serilog request logging, so
each rejected request produces one structured line carrying its status and path — the "sampled
request logging" this entry asked for, rather than a line per rejected field.

**Residual, accepted.** The line says *that* a request was rejected with 400 and where, not *which
field* failed. That detail is in the `ValidationProblemDetails` response body and is deliberately not
logged: a request with twenty bad fields would otherwise produce twenty lines, and the malformed
traffic this exists to reveal is exactly the traffic that would generate them. Distinguishing a
validation 400 from a handler 400 needs no extra field — the handler logs its own Warning, so a 400
with no accompanying Warning came from the filter.

**`DbUpdateConcurrencyException` logs the least useful line under contention.** *Resolved
2026-10-04.* Each warning used to carry EF's full boilerplate including a documentation URL —
roughly 230 characters, and **55 of 144 log lines** in one verified Stock run. `DescribeForLog` was
never at fault: it walked the chain correctly and found nothing to walk to, because a
zero-rows-affected `UPDATE` is not a SQL error and there is no inner `PostgresException`. Contrast
the 2 unique violations, which logged `PostgresException 23505: duplicate key ...
(table=stock_reservations, constraint=ix_stock_reservations_order_id_stock_item_id)` — genuinely
diagnostic.

The deferral reason was that the fix belonged in **all three** handlers. That stopped being true when
the shared-library extraction landed: `Describe` now exists once, in
`AgenticShop.Shared/Errors/ProblemDetailsExceptionHandler.cs`, so the fix was one switch arm. It
emits `DbUpdateConcurrencyException: no rows updated, the concurrency token did not match`. Naming
the conflicting entities would be more useful still, but `Entries` can only be populated by real EF
internals, so that branch could not be tested without mocking EF Core — and an untested arm in the
shared error path is the one thing this file exists to prevent.
`AConcurrencyConflict_LogsOneConciseLineInsteadOfEFsBoilerplate` pins the result, in Stock alone,
because that is where the volume was measured.

### Testing

**Sequential state-machine tests do not prove concurrency safety.** *Info, but load-bearing.*
The measurements are in `ARCHITECTURE.md` §5.3 and they are unflattering: a sequential "confirm
twice → 409" test still passes with the `xmin` token deleted. Any new counter needs a parallel test.

**`ResetDatabaseAsync` truncates rows but does not reset sequences.** *Low.* Still irrelevant, and
now deliberately so: no service has an identity or sequence column, and `OrderNumber` was designed to
avoid needing one — it is derived from the order's immutable id and the placement date, so there is
no allocation to reset and no assertion on it can become order-dependent. See
`src/AgenticShop.Ordering/docs/DECISIONS.md` → O4. It will matter the moment anything else uses a
sequence. Applies to all three fixtures.

**No Testcontainers reuse.** *Low.* Each `dotnet test` starts a fresh container (~3–5s). Three
services now means **three** PostgreSQL containers concurrently whenever the whole solution is
tested, and the three projects run in parallel, so the peak is three at once alongside the compose
container. Worth knowing before CI.

**`coverlet.collector` is referenced but nothing consumes it.** *Low.* In all six test projects.
No coverage report, threshold or CI step. Either wire up `--collect:"XPlat Code Coverage"` or drop
the reference.

**A single 5xx was observed once under three-container load and never explained.** *Medium · Phase 1.*
During a full-solution run, `ParallelConfirmsOfOneReservationShipTheGoodsExactlyOnce` reported eight
409s and one 200 out of ten parallel confirms — so the tenth response was neither, and the only
remaining candidate is a 5xx, most plausibly a PostgreSQL deadlock or serialisation failure that the
handler's default arm reports as a 500.

It was **not** confirmed, and it has not reproduced: five subsequent full-solution runs were clean,
and the only unhandled exceptions logged anywhere in them were Ordering's fourteen deliberate 502
tests. Stock also passes reliably on its own — it needs the parallel load of three Testcontainers
plus the compose database to surface.

The assertion that hid it is fixed (see Resolved below), so a recurrence now fails on
"contention is a client-visible conflict, never a server fault" and names the real cause instead of a
mismatched tally. What remains open is the underlying question: **is there a genuine 5xx path under
contention?** Chasing it needs a reproducible load, which is Phase 1's job alongside retry policies —
and a retry would likely dissolve it rather than explain it.

**`CatalogApiFixture.DisposeAsync` is not exception-safe.** *Low.* If `_factory.DisposeAsync()`
throws, `_postgres.DisposeAsync()` never runs and the container leaks until Ryuk reaps it. Wants a
`try/finally`. **`StockApiFixture` and `OrderingApiFixture` both have one** — fixed forward rather
than copied, so Catalog is now the only one of three without it. Recorded here rather than in a
service file because it is a divergence *between* services, and `AGENTS.md` §10 says a later
service's improvement should be carried back to the earlier one.

**Structural guards are duplicated per service, by design.** *Info.* The full inventory, and what
each service's `ContractSchemaAlignmentTests` actually covers, is in `ARCHITECTURE.md` §5.5.
Sharing them would require the test assemblies to reference each other, which is the coupling the
design avoids — so they are excluded from the Phase 1 shared-library extraction too.

### Cross-service consistency

**A confirm-phase failure strands stock, and a process crash can orphan it.** *Medium · Phase 2.*
Stock's `Confirmed` is terminal (Stock decision D3) and confirm decrements `quantity_on_hand`, so a
confirm that fails part-way through has no clean undo. Ordering records the outcome as a terminal
`PartiallyConfirmed` order and answers 502; the policy and its classification table are in
`src/AgenticShop.Ordering/docs/DECISIONS.md` → **O14** and that service's `ARCHITECTURE.md` §5.

Two residuals, both accepted for Phase 0:

- **A release that fails during compensation leaves the hold in place** until Phase 3's expiry
  worker. Detectable per order through Stock's `GET /api/v1/reservations?orderId=`.
- **If the Ordering process dies mid-confirm there is no order row at all**, because the row is
  written once at the end of placement. Stock then holds confirmed reservations against an order id
  that exists in neither database's queryable surface, and the endpoint above cannot find it either
  — `orderId` is required. Discovery needs a cross-database query for reservation order ids with no
  matching order. This is the dual-write problem; the Phase 2 outbox is what closes it.

Neither can oversell. Confirm decrements both counters and release decrements only `reserved`, so
`reserved <= quantity_on_hand` holds on every path including the failing ones. The exposure is
inventory *under*-availability, not negative stock — which is what makes both residuals survivable
while no money moves.

### Data and API

**Unknown JSON fields are silently ignored.** *Info.* Verified in Catalog: `{"bogus":1}` returns
201. Framework behaviour, so it applies to every service. Good for forward compatibility, but a
client typo like `"prices": 5` is accepted and silently falls back to the default.

*Narrower and sharper between services than into them.* Ordering deserialises Catalog's and Stock's
responses into records it declares itself, with only the fields it reads. So a field Catalog *adds*
is harmless, and a field Catalog *renames* yields a default rather than an error — a product with no
name, a reservation with no status. The status case is guarded, because a blank status would classify
as neither confirmed nor pending and would strand a hold silently; the name case is not.
Consumer-driven contract tests are Phase 3.

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

**PostgreSQL image tag is declared in four places.** *Info.* `docker-compose.yml` plus one
`TestPostgreSql.Image` per integration project. All three `ComposeConfigurationTests` fail if they
diverge from compose, so drift is caught rather than silent — but it is four places to edit, and a
fifth with any future service.

**No CORS policy.** *Info.* Irrelevant for service-to-service; will block a browser admin UI.

**`AllowedHosts: "*"`, no rate limiting, no HTTPS/HSTS.** *Info.* Correct for local Phase 0.
Tighten before any non-local deployment.

**`UserSecretsId` is declared but unused.** *Info.* All three services declare one;
`appsettings.Development.json` holds the local placeholder instead. It is documented in
`README.md` as the mechanism for anyone wanting different local values, which makes it meaningful
rather than decorative — but nothing enforces that.

**FluentAssertions 8.x licensing.** *Info.* Free for open-source, personal and educational use;
licensed above a revenue threshold commercially. Relevant if this becomes a portfolio piece
attached to commercial work. `Shouldly` (Apache-2.0) is the drop-in alternative.

### Deliberate, not defects

**Catalog-only conventions are not house style.** Soft delete, query filters and offset paging
belong to Catalog. Stock has none of them, and Ordering has none of them either — an order must never
vanish from a list, and there is no list to page. Details in
`src/AgenticShop.Catalog/docs/DECISIONS.md`.

---

## Resolved

Kept for the lesson, not the fix. These concern shared infrastructure or the repository itself;
service-specific findings are recorded with the service.

### An exact-count assertion hid a server fault behind a tally mismatch

`ParallelConfirmsOfOneReservationShipTheGoodsExactlyOnce` asserted `Count(409) == 9` **before** its
own no-5xx assertion. When one of ten parallel confirms came back as something other than 200 or 409,
the test failed with "found 8, expected 9" — naming the symptom and discarding the cause, because the
no-5xx assertion that would have identified it never ran. The exact count also contradicted
`AGENTS.md` §6 ("parallel assertions are **inequalities**, never exact success counts") and
`DECISIONS.md`, which rejects them as testing the scheduler rather than the design.

Fixed by reordering and by strengthening rather than relaxing: no-5xx first, then "every status is
either 200 or 409" (strictly stronger than a tally, since it also excludes 400/404/422), then
`Count(200) == 1` — the safety property that must never become an inequality. The same reordering was
applied to `AParallelBurstNeverOversells`, whose `(successes + conflicts) == 25` had the identical
hiding problem.

**Caught by:** a single failing full-solution run out of six. It did not reproduce, which is exactly
why the assertion order matters — an intermittent failure that reports the wrong thing is
indistinguishable from a flaky test, and gets retried until it goes green.

### The rest, as an index

Each of these is fixed and its *mechanism* is documented where the code now lives. What is recorded
here and nowhere else is **what caught it** — which is the transferable part, because every one of
them shipped past a green test suite.

| Finding | Caught by | Mechanism now documented in |
|---|---|---|
| Minimal APIs never validated body DTOs → an 84-char SKU returned 500 | manual probing during review, **after** the suite reported green | `ARCHITECTURE.md` §4.6 |
| Four binding failures (malformed JSON, empty body, non-numeric body field, non-numeric **query** param) returned 500 | systematic probing of every error path | `DECISIONS.md` → `BadHttpRequestException` |
| Nested collections were never validated — the filter recursed by property, and `List<T>` reflects as `Count`/`Capacity` | designing a test case for two collection levels | `ARCHITECTURE.md` §4.6 |
| Integration tests ran against the developer's own database — **25 tests passed while truncating it** | an E2E smoke check finding a stray row, not the suite | `ARCHITECTURE.md` §5.4 |
| The service boundary was asserted in prose, not enforced — the bootstrap role was a superuser | running the claim instead of reading it | `ARCHITECTURE.md` §2.2 |
| PostgreSQL published on `0.0.0.0` with a committed superuser password | `docker port` during review | `DECISIONS.md` → loopback only |
| `ArgumentException.Message` leaked to clients as `detail`, live-observed as `price ('-5,00') … (Parameter 'price')` | reading an actual response body | `ARCHITECTURE.md` §4.7 invariant 3 |
| Handled rejections logged EF's "see the inner exception" wrapper instead of the `SqlState` | reading a real log line | `DECISIONS.md` → log the whole chain |
| A routine duplicate-key 409 emitted ~40 lines of Error-level stack trace | a task-notification log tail | `DECISIONS.md` → handler decides severity |
| `UseXminAsConcurrencyToken()` does not exist in Npgsql 10 | the compiler | `ARCHITECTURE.md` §4.3 |
| `UseSnakeCaseNamingConvention()` placement · no `JsonContentAs<T>()` · `HttpValidationProblemDetails` namespace · obsolete parameterless `PostgreSqlBuilder()` · inaccessible `ValidationProblemHttpResult` · read-only `PostgresException` fields | the compiler | `ARCHITECTURE.md` §5.4 |
| `dotnet run` silently overrides the environment; a smoke test ran migrations and crashed | a startup crash | `DECISIONS.md` → `launchSettings.json` |
| Cross-cutting code was copied into all three services (~500 lines each), so a fix to the skeleton had to be applied three times — and the copies had already drifted in their doc comments | the 2026-09-24 decision's own trigger, fired before the third copy was written | `DECISIONS.md` → shared infrastructure library |
| Moving `IRequestContract` into `AgenticShop.Shared` silently detached two structural guards from the DTOs they were meant to scan — `typeof(IRequestContract).Assembly` no longer named a service assembly, so `RequestContractCoverageTests` and `ContractSchemaAlignmentTests` would have matched nothing and passed | `TheCoverageCheckIsNotPassingVacuously`, during the extraction | `AGENTS.md` §10 |

Two patterns are worth more than any single row. **Not one of the original twelve was caught by the
test suite** — the suite was green for all of them, and only three surfaced mechanically at all (two
compiler errors and a startup crash); the other nine needed a human to read an actual response body,
log line, `docker port` output or stray database row, or to probe a path nobody had thought to assert
on.

The last row is the counter-example, and the more useful lesson: a guard that asserts its own
non-vacuity turned a *silent* hole into a red build at the exact moment it opened. That is why every
reflection-based guard here carries one.
