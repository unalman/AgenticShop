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
recovers by choosing a new key. Phase 3's reservation-expiry worker is the mechanism that lifts it,
and it is the same shape of problem as a stranded hold.

The `orderId` extension on 409 and 502 bodies still does its separate job: a client that *reads* a
failure can query the order rather than guessing.

**A `PartiallyConfirmed` order whose id was never received is undiscoverable through the API.**
*Medium · Phase 2.* There is no list endpoint (decision O12), so the only handle on an order is its
id. If the 502 response was lost, the order exists in the database and cannot be reached over HTTP;
finding it needs `psql`, and reconciling it needs Stock's
`GET /api/v1/reservations?orderId=` with an id that is, by hypothesis, unknown. The cross-database
query that would find it is the same one the orphan residual in
`../../../docs/KNOWN-ISSUES.md` needs.

### Resilience

**A slow dependency is worse than an absent one.** *Medium · Phase 1.* There is a ten-second timeout
and nothing else: no retry, no circuit breaker, no bulkhead. A Catalog that is degraded rather than
down will hold every placement for ten seconds and then fail it, with no back-pressure and no way to
shed load. Under that condition Ordering consumes a request thread per placement for the full
duration. Correct for Phase 0 — it is exactly the problem Phase 1 exists to feel — but it is the
first thing that will hurt under any real load.

**Compensation is best effort and can strand a hold.** *Medium · Phase 3.* A release that fails is
logged and does not change the order's classification, because the classification describes what
shipped and a stranded hold ships nothing. The hold then stays in Stock's `reserved` until something
lifts it, and in Phase 0 nothing does: there is no `ExpiresAtUtc` and no expiry worker. The exposure
is inventory *under*-availability, never negative stock, so it is lossy rather than unsafe.

### Testing

**The outbound correlation header is proven by hand, not by the suite.** *Low.* Every integration
test replaces `ICatalogClient` and `IStockClient` at the seam, which is the right thing for scripting
failures and the reason no automated test can observe what actually goes on the wire.
`CorrelationIdPropagatingHandlerTests` covers the handler in isolation; the `AddHttpMessageHandler`
attachment that puts it in the real pipeline was verified once, live, by finding Ordering's inbound id
in Stock's log line (recorded in `docs/ARCHITECTURE.md` §6). A regression in that one registration
line would not fail any test.

**A malformed downstream response is only partly covered.** *Low.* The clients throw
`InvalidOperationException` on a 2xx they cannot read, which the handler answers as a 500. No test
produces one, because producing it needs a real HTTP server returning a bad body — the fakes cannot
malform a response they never serialise.

**Contract drift with Catalog and Stock is invisible.** *Medium · Phase 3.* `CatalogProduct` and
`StockReservationSnapshot` are declared here, deliberately, and deserialisation ignores unknown
fields. So Catalog adding a field is safe, and Catalog *renaming* one silently yields a default value
— a product with no name, a reservation with no status. The status case is guarded
(`ListByOrderAsync` refuses a read with a blank status, because it would classify as neither confirmed
nor pending); the name case is not. Consumer-driven contract tests are Phase 3.

### Deliberate, not defects

**`xmin` is declared on both entities and never fires.** Phase 0 has no update path: the row is
written once, already terminal. The token is mandatory on every entity and Phase 2's saga will need
it, so it is declared now rather than added by a migration later. The consequence is that the
handler's `DbUpdateConcurrencyException` arm is unreachable and no test can exercise a real conflict.

**No list endpoint, no cancel, no mutation of a persisted order.** Decision O12. Symmetry with Catalog
is not a reason, and a cancel route would need a status, a compensation path for confirmed lines that
does not exist, and an update path that would make `xmin` and `UpdatedAtUtc` load-bearing.

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
