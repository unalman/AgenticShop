# Known issues — Ordering Service

Issues specific to Ordering. Repository-wide and shared-infrastructure items live in
`../../../docs/KNOWN-ISSUES.md`; Catalog's in `../../AgenticShop.Catalog/docs/KNOWN-ISSUES.md`;
Stock's in `../../AgenticShop.Stock/docs/KNOWN-ISSUES.md`.

Severity: **High** would mislead or harm if replicated · **Medium** a correctness or observability
gap · **Low** hygiene · **Info** a trade-off worth knowing.

---

## Open

### Consistency

**A stranded idempotency claim blocks that key forever.** *Low · Phase 3.*
`POST /orders` now requires an `Idempotency-Key` and a repeated key replays the recorded outcome, so
the largest residual this service had is closed — see "Resolved" below. What replaces it is smaller
and fails in the safe direction: if the process dies between claiming a key and writing the order,
the claim never completes and every later attempt with that key gets a 409. There is no worker to
notice, and no endpoint can clear it.

This is deliberate rather than an oversight. Treating an old claim as abandoned would need a clock
threshold, and two requests could both decide the claim was abandoned and both place — the exact
failure the key exists to prevent. Refusing blocks one key and can never double-place, so the client
recovers by choosing a new key. Phase 3's expiry worker is the mechanism that lifts it, and it is the
same shape of problem as an order stuck `Pending`.

The `orderId` extension this item used to point at as a partial comfort is no longer emitted at all —
nothing writes a row and then fails inside the request, so there is no id to echo. See
`ARCHITECTURE.md` §9.

**An order that never settled is undiscoverable through the API.** *Medium · Phase 3.* There is no list
endpoint (decision O12), so the only handle on an order is its id — and `POST /orders` returns that id
in a 202 `Location` header, so an order whose 202 was lost exists in the database and cannot be reached
over HTTP. Finding it needs `psql`, via `status = 'Pending' AND updated_at_utc IS NULL`. The status that
used to be the residual here, `PartiallyConfirmed`, cannot be written any more: Stock takes every line
of an order or none, so an outcome is never partial. The cross-database query that would find either is
the same one the orphan residual in `../../../docs/KNOWN-ISSUES.md` needs.

### Resilience

**Worst-case placement latency roughly tripled.** *Low · accepted 2026-10-04.* Retry, a circuit
breaker and timeouts landed in Phase 1 (decision O18), which closed the item that used to sit here —
"a slow dependency is worse than an absent one", with a ten-second timeout and no back-pressure at
all. It bought transient-failure absorption and load shedding, and it cost latency: a downstream call
that times out on every attempt now spends up to the 35-second total timeout instead of 10 seconds,
because two retries follow the first attempt. A degraded Catalog therefore holds a placement longer
before failing it than it used to, while a *blipping* one now succeeds where it previously failed.
Judged the right trade for a synchronous placement path — the caller is waiting either way, and
stock is held for the duration in both cases — but it is a real change and not a free one. There is
still no bulkhead, so nothing caps concurrent placements; the standard resilience pipeline ships one
and it was deliberately not enabled.

*Measured 2026-10-04*, with Catalog not running at all: **14.3 s** end to end for one placement. Each
of the three attempts cost ~4.1 s rather than failing instantly, which on Windows is what a connect to
`localhost` with no listener does — `::1` is tried before `127.0.0.1`. So the per-attempt cost of an
*absent* dependency is platform-dependent and nowhere near zero, and the 10 s attempt timeout is a
ceiling rather than the typical case. Worth remembering before reading a slow placement as a slow
dependency.

**A dead dependency fills the log.** *Low · observed 2026-10-04.* One placement against an absent
Catalog produced **seven lines**: four Polly Warnings (an "Execution attempt" and an "OnRetry" event
per retry), a Polly **Error** for the final attempt, the handler's **Error**, and the access-log
Information line — most of them carrying a full stack trace. Two Errors for one classified failure is
one too many, and the Polly one is the duplicate: the handler's Error already names the request, the
status and the cause.

Left as it is, deliberately. The resilience category is not silenced — a retry that recovered is worth
seeing and is not an error — and `LoggingConfigurationTests.TheResiliencePipelineIsNotSilenced`
asserts that. Trimming only the final-attempt Error would need a filter keyed on Polly's event, which
is the same shape as the EF filter that could not work; unlike that one it is feasible, because Polly
does attach a result to its events. Worth doing if an outage ever makes the volume hurt. Note that
the access log is already excluded from this: `ServiceLogging.RequestLogLevel` keeps it at
Information, so a 502 does not add a third Error.

**Nothing is exempt from retry, and that is only safe because nothing writes.** *Info.* The
`NotRetryable` marker, the predicate that read it and the `ReserveMarksItsRequestAsNotRetryable` guard
were all deleted with `StockClient`. What remains is one client that only reads Catalog, so a retry
cannot duplicate a side effect. The hazard the exemption existed for is unchanged and is recorded in
O18: **if an outbound write is ever added, it must re-earn the exemption.** There is no marker left to
set, so reintroducing one is part of that work rather than a reason to skip it.

**There is no compensation, and the residual changed shape.** *Medium · Phase 3.* Ordering releases
nothing, because Stock's consumer holds all of an order's lines or none of them in one transaction — a
refused hold rolls back and leaves no `reserved` behind, so there is no stranded hold to compensate
for. What replaced it is milder and findable: an order accepted as `Pending` whose outcome never
arrives stays `Pending` forever, because there is no `ExpiresAtUtc` and no expiry worker. The query
that finds those rows is `status = 'Pending' AND updated_at_utc IS NULL`, since the outcome consumer
stamps `updated_at_utc` when it settles one. Tracked with its blast radius in
`../../../docs/KNOWN-ISSUES.md` → "Cross-service consistency".

### Testing

**Outbound trace propagation is proven by hand, not by the suite.** *Low.* Every integration test
replaces `ICatalogClient` at the seam, which is the right thing for scripting failures and the reason
no automated test can observe what actually goes on the wire. It was verified live instead — a
caller-supplied `traceparent` produced the same trace id in all three services' logs, with Catalog's
server span parented to Ordering's client span (recorded in `docs/ARCHITECTURE.md` §6). The broker hop
is covered where the HTTP one cannot be: `StockOutcomeConsumerTests` and Stock's
`OrderPlacedConsumerTests` both assert a `traceparent` survives the round trip.

The exposure changed shape when `CorrelationIdPropagatingHandler` was deleted. It used to be one
`AddHttpMessageHandler` registration line that no test covered; it is now the `instrumentHttpClient:
true` argument Ordering passes to `AddServiceTracing`, which no test covers either. Drop it and
`traceparent` stops being injected, the trace breaks at the first hop, and the whole suite stays
green. Same risk, different line.

**A malformed downstream response is only partly covered.** *Low.* The clients throw
`InvalidOperationException` on a 2xx they cannot read, which the handler answers as a 500. No test
produces one, because producing it needs a real HTTP server returning a bad body — the fakes cannot
malform a response they never serialise.

**Contract drift with Catalog is invisible.** *Medium · Phase 3.* `CatalogProduct` is declared here,
deliberately, and deserialisation ignores unknown fields. So Catalog adding a field is safe, and
Catalog *renaming* one silently yields a default value — a product with no name, which then reaches an
order line and is written to the database. Nothing on Catalog's side asserts the shape it must send.

`StockReservationSnapshot` went with the client that read it, so Stock's HTTP contract is no longer a
drift surface at all. The event contracts replaced it as the bigger one, and those *are* guarded in
both directions: `StockEventContractTests` asserts this service's copy of `StockCommitted` and
`StockReservationRejected` reads the exact field names Stock's `TheOutcomesSerialiseAsCamelCaseWithNamedEnums`
pins, and `OrderPlacedContractTests` does the same for `OrderPlaced` against a payload captured from a
live compose run. Two independent assertions on one wire format, so a rename on either side fails one
of them naming the field. Consumer-driven contract tests are still Phase 3; Catalog is what they would
add.

### Deliberate, not defects

**`xmin` no longer fires over HTTP, but it does fire in the consumer.** The handler's
`DbUpdateConcurrencyException` arm is still unreachable, because no endpoint updates a row — an order
is written once, as `Pending`. `StockOutcomeConsumer` updates one, settling `Pending` → `Confirmed` or
`Failed`, and handles the conflict the way Stock's consumer does: nack and requeue once, then drop and
log an error rather than spin. So the token is load-bearing on the messaging path and decorative on the
HTTP one, and the two are worth keeping straight — a test that provokes a real conflict now belongs in a
messaging collection, not the API one.

**No list endpoint, no cancel, no mutation of a persisted order over HTTP.** Decision O12. Symmetry with
Catalog is not a reason, and a cancel route would need a status and a compensation path for confirmed
lines that does not exist. The consumer's settle is the one mutation, and it is not a route.

**Duplicate product lines are combined silently.** Decision O6. A client that sends the same product
twice gets one line back with the summed quantity. That is a visible change to what was submitted, and
it is correct only because the unit price comes from Catalog and is therefore identical for both. If
per-line pricing ever arrives — a promotion on one line and not another — combining stops being
lossless and this becomes a bug.

**`OrderNumber` can collide.** Decision O4. Eight hex characters per day, so roughly one collision per
four billion orders on a single date. The unique index turns it into a 409 the client cannot fix.
Accepted; a sequence would have reintroduced the fixture problem the derived number avoids.

---

## Resolved

Kept for the lesson, not the fix. Findings about shared infrastructure are recorded in
`../../../docs/KNOWN-ISSUES.md`.

### A retried `POST /orders` placed a second order and a second set of holds

*Closed 2026-10-03, Phase 1.* There was no idempotency key, so a client that retried after a
timeout — or after a 502 whose body it failed to read — placed a genuinely new order. For a first
attempt that reached `Confirmed` that meant shipping twice. It was the single largest residual in
the repository, and the reason idempotency keys precede retry policies in `docs/ROADMAP.md` rather
than following them: retrying without a key turns an occasional duplicate into a systematic one.

Fixed by a required `Idempotency-Key` header, a claim row committed before the first side effect,
and a completion written in the same transaction as the order — decision **O17**.

**Caught by:** reasoning about what a client does when it never receives a response, not by any
test. No test in the suite could have failed on it, because every test that placed an order placed
it exactly once and read the result. The residual was invisible until someone asked what a *retry*
would do — which is the general lesson: a suite that only exercises the happy path once per test
cannot see a duplicate-submission bug, and "what does the caller do when the response is lost" is a
question worth asking of every write endpoint.

**What replaced it** is a smaller residual in the safe direction, and it is still open: a claim
stranded by a crash blocks that one key forever. See "Open" above.

### `Uri.TryCreate(..., UriKind.Absolute)` accepts `"localhost:5082"`

The startup validation for the downstream base URLs checked only that the value was an absolute URI.
`localhost:5082` *is* one: `Uri` reads `localhost` as the scheme and `5082` as the path. So a base URL
missing its `http://` would pass validation, and then fail on the first outbound call — mid-placement,
after stock had been reserved, which is the exact moment a configuration error is most expensive.
Fixed by requiring the scheme to be http or https.
**Caught by:** the test written for the validation, which failed on first run and was right to.

### `dotnet run --environment Development` does not set the environment

There is no such switch. Everything after the project name is passed to the application, and
`--environment` is not a host configuration key the command line maps to, so the service started in
Production, `appsettings.Development.json` was not loaded, and startup failed on the missing
connection string. That failure is the configuration guard working exactly as designed — but it reads
like a broken service. `launchSettings.json` is what pins the environment, and `--no-launch-profile`
removes both it *and* the fixed port. Documented in `AGENTS.md` rather than fixed; there is nothing to
fix. **Caught by:** three services failing to start during the live smoke test.

### `dotnet ef migrations add` defaults to `Migrations/`, not `Data/Migrations/`

The house convention is per service under `Data/`, which Catalog and Stock both follow, and which
`--output-dir Data/Migrations` is required to produce. The first attempt landed in the wrong place and
had to be removed and regenerated. **Caught by:** globbing for the file afterwards instead of assuming
it was where the convention says.

### A test helper named `Order` shadowed the `Order` entity

`OrderApiTestBase.Order(params ...)` made every `Order.MaxLines` inside a derived test class a method
group rather than a constant, producing CS0119 at four call sites. Renamed to `OrderWith`.
**Caught by:** the compiler.

### A primary-constructor parameter used both to initialise a property and to close over it

`OrderApiTestBase(fixture)` assigned `Fixture { get; } = fixture` and then wrote `Client =>
fixture.Client`, which captures the parameter into the enclosing type's state as well — CS9124.
Everything now reads through the property, which also gives the fixture one owner.
**Caught by:** the compiler.

### `TryAddWithoutValidation` appends rather than no-ops

The propagating handler added the correlation header unconditionally. If a request already carried one
— a default header on the client, say — it would have gone out with two values, and a downstream
service reading `FirstOrDefault()` would have picked arbitrarily. Guarded with a `Contains` check: a
`DelegatingHandler` sits in a pipeline it does not own and should not corrupt a message it did not
create. **Caught by:** the unit test asserting the caller's own value survives.

### `Write-Output` inside a helper whose result is discarded

The first live smoke run printed nothing for any of its failure-path checks. `$null = Post-Failure …`
assigns the function's *entire output stream* to null, and `Write-Output` writes to that stream.
Switched to `Write-Host` for progress. The service was correct throughout; the script was not.
**Caught by:** noticing four expected lines were missing rather than assuming the checks had passed.
