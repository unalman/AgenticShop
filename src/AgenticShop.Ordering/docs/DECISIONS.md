# Decisions — Ordering Service

Why Ordering is built the way it is. Global decisions — platform, topology, boundaries, the shared
error contract, validation, testing strategy, security — live in `../../../docs/DECISIONS.md` and
are not repeated here. Stock's D1–D14 govern the reservation behaviour this service depends on and
are cited where they constrain a choice below.

O1–O4 were approved by name before implementation. The rest record the decisions the plan left to
implementation plus the ones the failure-path analysis forced; **O3 was revised and O14–O16 added
on 2026-09-24**, when a failure or timeout during the reserve and confirm phases was analysed and
found to have no clean undo. Design detail lives in `ARCHITECTURE.md` alongside this file.

---

## O1 · One `OrderPlacer` collaborator, and no service layer beyond it

**Alternative rejected:** putting the orchestration in the endpoint handler, or introducing an
`Application`/`Services` layer.

**Why.** `docs/DECISIONS.md` → "No repository, service or `Application` layer" pre-authorises its own
revisit for "a use case that needs to coordinate two aggregates or an external call inside one
transaction, and names `POST /orders`". This is that case. The endpoint stays thin — bind, call,
return 201 — and everything that has to be reasoned about lives in one testable place.

**What it deliberately is not.** No interface, no second consumer, one public method, and it lives in
`Endpoints/` beside the only thing that calls it rather than in a new folder. It is a handler's
collaborator, not a layer. It cannot live in `Domain/`, because the domain must not reference EF Core
or the clients.

**Consequence.** Its logic is tested at the integration level with faked clients rather than unit
tested, because it needs a real `DbContext` and EF Core is never mocked. That is the correct split,
not a gap.

## O2 · Confirm at placement, synchronously

**Alternative rejected:** reserve at placement and settle later, from a worker or an event.

**Why.** Phase 0 has no broker and no worker, so "later" has no owner. Reserving without settling
would leave every order holding stock indefinitely and would make `Reserved` a number that only ever
grows. Settling inside the request is also what makes the confirm-phase problem visible now rather
than after a queue exists to hide it behind.

**Revisit.** Phase 2 replaces this with `OrderPlaced` → Stock consumes → `StockReserved`, at which
point the order is written as `Pending` first and a saga drives it.

## O3 · Statuses `Pending | Confirmed | Failed | PartiallyConfirmed` — **revised 2026-09-24**

Approved as three statuses. The fourth was added when the confirm phase was analysed, because none of
the three can truthfully describe "some lines shipped and cannot be un-shipped".

**Alternatives rejected.**

- **`Failed`.** Says nothing shipped, so a client that believes it starts over. This is the one
  genuinely dangerous answer available, and a three-status vocabulary forces it. Phase 1's
  idempotency key (O17) narrows the danger but does not remove it: the key stops a client that
  retries *the same request*, and a client told "nothing shipped" will deliberately choose a
  **fresh key** and re-order — which the key cannot and should not block.
- **`Pending`.** Phase 0 has no worker, so a `Pending` row is a row stuck forever, and it would break
  the property that a completed placement leaves the order terminal.
- **A `FailureReason` column instead of a status.** More surface, not less: consumers branch on
  `Status`, and a second vocabulary that has to stay in sync with it costs more than one enum member
  plus one `CHECK` value.

**And the constraint that follows from it: Phase 0 never persists a `Pending` order.** The row is
written exactly once, after reservation, confirmation and reconciliation have all finished, and it is
written already terminal. `Pending` stays in the enum and in the `CHECK` as the construction state and
for Phase 2's saga.

**Consequence accepted.** A status that is never persisted is odd on its face, and
`APersistedOrderIsAlwaysTerminalBecauseNothingWritesItAsPending` exists so the oddity is asserted
rather than discovered.

## O4 · `OrderNumber` is derived, not sequenced

`ORD-yyyyMMdd-xxxxxxxx`, where the suffix is the first eight hex characters of the order's immutable
id. Unique-indexed.

**Alternative rejected:** a PostgreSQL sequence or an identity column.

**Why.** The number is a pure function of values that already exist, so nothing has to be allocated,
nothing can be skipped by a rolled-back transaction, and no database round trip is needed before the
reserves can start — which matters, because Stock's reservations carry the order id.

**Second reason, and the one that settled it.** `ResetDatabaseAsync` truncates rows but does not reset
sequences, an open item in `../../../docs/KNOWN-ISSUES.md`. A sequence would have made every assertion
on `OrderNumber` order-dependent across a test run. Deriving the number means that problem is never
created.

**Cost accepted.** Eight hex characters is 4 billion values per day before a collision is possible,
and a collision is a `23505` → 409 that the client cannot fix. Judged acceptable against the
alternative, and the unique index makes it detectable rather than silent.

**Also:** the date is formatted with `CultureInfo.InvariantCulture`, not optionally. This host is
Turkish-locale and a culture-sensitive format has already leaked a server-formatted value into a
response once.

## O5 · `ProductName` and `UnitPrice` are snapshotted onto the line

A standing project invariant, not a Catalog convention: a later catalog edit must not rewrite order
history. `Currency` is snapshotted onto the order because one order has one currency.

**Cost accepted.** Ordering stores data it does not own, and the two can disagree. That is the point —
the order is the record of what was agreed, and Catalog is the record of what is on sale now.
`ALineSnapshotsTheCatalogSoALaterEditCannotRewriteOrderHistory` changes the product after the order
exists and asserts the order did not move.

**Not snapshotted:** the SKU. Nothing in Phase 0 reads it, and a field nobody uses is a field that can
silently go stale.

## O6 · Duplicate product lines are combined, not rejected

**Why.** Stock's `UNIQUE(order_id, stock_item_id)` allows one hold per product per order (decision
D2), so a second line for the same product would simply be refused — and the refusal would look like
an out-of-stock error to the caller. Combining is lossless: the unit price comes from Catalog and is
identical for both lines.

**Where it happens.** Before resolution, not before persistence, so a repeated product costs one
Catalog call rather than two. `Order.Create` still guards against duplicates reaching it, so
forgetting to combine is a loud failure rather than a refused reservation.

## O7 · Reservation ids are never persisted

**Alternative rejected:** a `ReservationId` column on `OrderLine`.

**Why.** Stock owns them. A copy here is a second source of truth that can drift, and it is not
needed: the placement path holds them in memory for the few milliseconds it uses them, and every
after-the-fact reconciliation goes through `GET /api/v1/reservations?orderId=`, which reports the
product id each line already carries. This is the same reasoning Stock applies to not storing
`product_id` on a reservation.

**Consequence.** Reconciliation matches on `ProductId`, not on a reservation id. `ReconcileAsync`
therefore works even for a hold whose 201 was lost and whose id nobody ever saw — which a persisted
column could not help with, since there would have been nothing to persist.

## O8 · One currency per order, enforced during resolution

**Why.** `TotalAmount` is a sum. Across currencies it is a meaningless number written into permanent
order history, and nothing downstream would ever detect it.

**Why at resolution.** Before the first mutation, so it costs no compensation and writes no row.

**The 400 carries `detail: null`.** A composed detail listing the clashing codes was considered and
rejected on review. The case for it was that "the request is invalid" with no indication of why is
not actionable, and that currency codes are our own vocabulary rather than an exception `Message`.
It loses because invariant three and the repository's domain-400 convention both say null, and one
service inventing a second 400 shape is a divergence with no forcing reason — Phase 1's shared
handler would have to accommodate it. The codes stay on the exception's typed property and reach the
operator through the log line.

## O9 · Ambiguous failures are resolved by a read, never by a status code or response text

**Why.** Stock answers 409 on confirm both for "already confirmed" — which decision D3 says must be
treated as success — and for an xmin conflict, which must not. The shared error contract forbids
parsing another service's `detail` to tell them apart, and guessing wrong in either direction is
expensive: treating a conflict as success ships nothing and reports that it did, while treating an
applied confirm as a failure invites a re-order.

**So:** `GET /api/v1/reservations?orderId=` after any ambiguous confirm. It is authoritative, it is
keyed by order id, and it reports the product id every line can be matched on.

**This is not a retry, and does not reopen decision B.** A read mutates nothing. "No server-side retry
in Phase 0" is about re-issuing a mutation, and remains true.

**Cost accepted.** One extra round trip on the failure path only. The clean reserve-refusal path — the
hot one under contention, where Stock measured 36 of 40 requests losing the xmin race — deliberately
does *not* read: it knows exactly which holds it took and that they are still `Pending`, so it
releases them directly.

## O10 · Never release a `Confirmed` reservation

Stock refuses it (`InvalidReservationStateException` → 409). More importantly it would be *wrong*
even if allowed: release decrements `reserved` without incrementing `quantity_on_hand`, so it would
leave counters describing inventory that has already left.

**Corollary.** A 409 from `release` is never treated as success without a read. It means either
"already released" (benign) or "xmin conflict" (the hold survives), and only the current state
distinguishes them. Both are logged; neither changes the classification.

## O11 · A 502 or 409 that wrote a row echoes the order id

Added as a ProblemDetails extension alongside `correlationId`.

**Why.** A failure body that names no order leaves the client unable to distinguish "nothing was
created" from "something was created and is broken", and that ambiguity is precisely what turns one
failure into a duplicate shipment. The extension costs one line in the handler and is the difference
between an actionable failure and a guess. It also survives O17: an idempotency key makes a *retry*
safe, but a client that cannot see the order cannot decide whether retrying is even the right move.

**Cost accepted.** The handler had to return more than a status/title/detail triple. The Phase 1
extraction resolved it by putting an optional `Extensions` dictionary on the shared
`ExceptionClassification`, so `orderId` is data on a shared type rather than a fork of it — and
neither Catalog nor Stock, which have no order to name, carries anything they do not need.

## O12 · No list endpoint, and no route that mutates a persisted order

`POST /api/v1/orders` and `GET /api/v1/orders/{id}` are the whole surface.

**Why no list.** A status-filtered list would be the operator's way into `PartiallyConfirmed` orders,
but it would need paging to be safe, and Catalog's paging is explicitly not house style — Stock has no
list endpoint for the same reason. Phase 0 does not need one: the failure bodies carry the order id.
Phase 2's saga needs a durable work queue, which is a different thing from a paginated read.

**Consequence accepted, and it is a real one.** A `PartiallyConfirmed` order whose id the client never
recorded — the response was lost — is undiscoverable through the API. Finding it needs `psql`.
Recorded in `docs/KNOWN-ISSUES.md` rather than solved with an endpoint nothing else would use.

**Why no mutation route.** It is what makes the row write-once, `Pending` unpersisted, and an illegal
transition unreachable from outside. A cancel route would need a `Cancelled` status, a compensation
path for confirmed lines that does not exist, and an update path that would make `xmin` and
`UpdatedAtUtc` load-bearing — all of it Phase 2 work arriving early.

## O13 · `PlacedAtUtc` only, and `xmin` declared but inert

**Why no `UpdatedAtUtc`.** The status is decided before the single `SaveChangesAsync`, so an
`UpdatedAtUtc` could only ever equal `PlacedAtUtc`. A column that implies an update path which does
not exist is worse than no column: it invites code that uses it.

**Why `xmin` anyway.** Optimistic concurrency is mandatory on every entity, and Phase 2's saga drives
an order through several states. Adding the token then would mean a migration and a window without
protection. Declaring it costs no storage and emits no DDL.

**Honest about the consequence:** the token never fires in Phase 0, so the handler's
`DbUpdateConcurrencyException` arm is unreachable and untested by any real conflict. It is kept
because the arm is part of the copied skeleton and because Phase 2 will need it, and the divergence
between "declared" and "exercised" is recorded rather than hidden.

## O14 · Confirm-phase policy — **added 2026-09-24**

A failure or timeout during the confirm phase ends the order in terminal `PartiallyConfirmed` and
answers 502. The classification is decided by the reconciliation read in O9, never by a status code
or response text. The step sequence and the classification table are in `ARCHITECTURE.md` §5 — that
is the canonical statement; it is not repeated here.

Three parts of it are decisions rather than mechanics:

- **A fourth status, not a reuse of the other three.** `Failed` says nothing shipped, so a client
  that believes it starts over — and the confirmed lines ship a second time. That is the one
  genuinely dangerous answer available, and the one a three-status vocabulary forces. O17's
  idempotency key does not make this safe: it blocks a repeated *request*, not a client that
  deliberately picks a new key because it was told nothing happened. `Pending` would never advance,
  because Phase 0 has no worker. A
  `FailureReason` column is more surface, not less: consumers branch on `Status`, and a second
  vocabulary that must stay in sync costs more than one enum member plus one `CHECK` value.
- **`PartiallyConfirmed` is reachable only from the confirm phase.** A reserve-phase fault cannot
  have shipped anything, so its worst case is a stranded hold and the order is `Failed` even when
  the read also failed. Without that asymmetry the fourth status would be routine rather than rare.
  The reserve-phase policy is **O16**.
- **A release that fails during compensation does not change the classification.** The status
  describes what shipped, and a stranded hold ships nothing. It is logged and left for Phase 3's
  expiry worker.

**Revisit.** Phase 2, when a saga can drive a `PartiallyConfirmed` order to a resolved state instead
of recording it.

## O15 · Cancellation stops at the first side effect — **added 2026-09-24**

Resolution against Catalog honours the caller's token; from the first reserve onward every call uses
`CancellationToken.None`, including the final `SaveChangesAsync`.

**Why.** A client that hangs up mid-placement must not leave stock held against an order nobody
recorded. The forward calls are still bounded — by the resilience pipeline's timeouts and EF's
command timeout — so this cannot hang a request.

**Why the timeout is set at all.** Ten seconds, and it exists so that "timed out" can happen. With
the 100-second default the whole confirm-phase policy would be unreachable in practice, and the
failure mode O14 exists for would never arrive. **Superseded in mechanism, not in value:** the bound
is now the resilience pipeline's per-attempt timeout, still ten seconds — see O18 for why
`HttpClient.Timeout` had to stop being set.

## O16 · Reserve-phase failure — fail fast, compensate, never retry — **added 2026-09-24**

O14 covers a *confirm* that fails. This is the *reserve* counterpart, and it is the cleaner case:
nothing has shipped and every hold is still `Pending`, so releasing them all is a complete undo.

**Decision.** If a later line's reservation fails after earlier lines were reserved, Ordering
**releases every reservation already created for that order and fails it**. There is **no
server-side retry in Phase 0.**

**Why this is a decision at all.** Stock does not retry on `xmin` conflict (decision D9), so a
reservation can fail even when stock was available — measured at 36 of 40 requests losing the race
against 10 units. Ordering must therefore *expect* a reserve to fail.

**Alternatives rejected.**

- **Retry.** Stock's reserve is idempotent per `(order_id, stock_item_id)` (decision D2), so
  re-issuing one line returns 409 rather than double-holding — but a retry loop that reorders or
  re-issues lines can still strand holds, and `AGENTS.md` §8 defers resilience libraries precisely
  because retrying without idempotency keys double-reserves stock. Retry is Phase 1 work, after keys
  exist.
- **Partial-fill.** Silently changes what the customer ordered. That is a product decision, not an
  infrastructure one, and nothing in Phase 0 asks for it.

**Why fail fast anyway.** It is honest about Phase 0's guarantees, and the release-all-then-fail
sequence is the synchronous prototype of saga compensation — so the shape carries into Phase 2
rather than being thrown away.

**Consequences to design for.** Compensation must tolerate a release that fails, because a failed
release leaves stock held against an order that no longer exists; Stock's
`GET /api/v1/reservations?orderId=` (decision D10) exists for exactly this reconciliation. And "409
because already released" must be treated as success, mirroring the confirm rule in O9 — but only
after a read, per O10.

**Revisit.** Phase 1, once idempotency keys make a retry safe. **That prerequisite was met** — see
O17 — and the retry work is now done, see **O18**. Note what O18 did *not* change: this decision's
"no server-side retry" is about re-issuing a **reserve**, and reserve is the one outbound call O18
deliberately exempts from retry. The reserve phase still fails fast and compensates.

## O17 · Idempotency keys on `POST /orders` — **added 2026-10-03, Phase 1**

The header is **required**, and a repeated key replays the recorded outcome instead of placing again.

**Why required rather than optional.** An optional key leaves the residual open for every client
that does not send one, and the residual was the largest in the repository: a retried placement
created a second order and a second set of holds, which for a first attempt that reached
`Confirmed` meant shipping twice. Neither Stock's `UNIQUE(order_id, stock_item_id)` nor Catalog's
unique SKU could help, because a retry carries a *fresh* order id — only the caller knows that two
requests are the same request. This is a breaking contract change and it is worth it: there are no
external consumers, and a guarantee that callers can opt out of is not a guarantee.

**The claim is written after resolve and before reserve.** That position is the whole design:

- Resolve is pure reads, so an unorderable product or a mixed currency fails **before** the key is
  consumed. The caller can fix the basket and retry the same key. Claiming earlier would burn a key
  on a mistake the caller can still correct.
- Reserve is the first side effect, so the claim must be committed before it. Claiming later would
  leave a window in which two concurrent requests both hold stock for the same key.

Validation failures are earlier still — they are rejected in the endpoint filter, before the
handler — so they consume nothing either.

**The completion is written in the same `SaveChangesAsync` as the order.** One transaction, so there
is no window in which an order exists without its key pointing at it. That window is exactly what a
retry would exploit: a key written afterwards could be lost to a crash, and the next attempt would
place a second order.

**The status code is stored, not derived from `Order.Status`.** The mapping is not one-to-one: a
`Failed` order came from either a 409 (stock refused) or a 502 (Stock unreachable during reserve).
Deriving 409 from "Failed" would tell a client to fix its basket when the real problem was ours —
and would invite the retry this decision exists to prevent.

**Replay rebuilds the body rather than storing it.** The status is replayed verbatim; for a 201 the
body is byte-identical to the original, because the row is write-once and `OrderResponse.From` is a
pure function of it. For a failure the detail text differs and says that this is a replay. Storing
serialised response bodies would have created a second source of truth that could drift from the
row it describes, which is the same reason `OrderNumber` is derived and reservation ids are never
persisted.

**A claimed-but-uncompleted key answers 409, and fails closed.** If the process dies between the
claim and the write, the key is stranded: every later attempt is refused. That is deliberate. The
alternative — treating an old claim as abandoned — needs a clock threshold, and two requests can both
decide the claim is abandoned and both place, which is the exact failure the key prevents. Refusing
blocks one key and can never double-place. Phase 3's expiry worker lifts it, the same mechanism that
lifts a stranded hold.

**Rejected.**

- *Deriving the key from a hash of the body.* Cannot distinguish an intentional re-order of the same
  basket from a retry, so it would refuse legitimate repeat purchases.
- *Storing the response body.* A second source of truth; see above.
- *A separate surrogate primary key with a unique index on the key.* The row's whole purpose is that
  a given key resolves to at most one order, and a primary key on the key says that directly.
- *Blocking the second request until the first settles.* Holds a request thread for the duration of
  a placement that may already be dead, and needs a timeout whose expiry is the same ambiguity again.

**Consequence for the retry work.** This is the prerequisite O16 and `docs/ROADMAP.md` named. Retry
policies on the typed clients are now safe to add, because a retried placement that reaches Ordering
again under the key replays rather than duplicates. Note that it makes *inbound* retries safe;
the outbound calls to Stock are still covered by Stock's own natural idempotency (decision D2) and
by the reconciliation read (O9). **What that turned out to mean in practice is O18.**

## O18 · Retry is per request, and reserve is exempt — **added 2026-10-04, Phase 1**

`Microsoft.Extensions.Http.Resilience` on both typed clients: a per-attempt timeout, a total
timeout, retry, and a circuit breaker. Retry is enabled **per request**, not per client and not per
HTTP method, and `StockClient.ReserveAsync` opts its request out.

**Why reserve is exempt.** This is the part O17's closing note left to find out. D2 does make Stock's
reserve idempotent — a repeated `(order_id, stock_item_id)` cannot double-hold — but idempotence on
the server is not the same as a safe answer on the client. Stock reports the duplicate as a **409**
from its unique index, and `ReserveAsync` reads 409 as "this line cannot be held". So if a first
attempt committed and only its 201 was lost, a retry converts a hold that exists into a reported
refusal, and two things follow:

- the order is written `Failed` with 409 and the caller is told stock was unavailable, when it was
  in fact taken;
- the refusal path deliberately skips the reconciliation read (O9 keeps it off the contention hot
  path), so it releases only the holds it knows about — and the first attempt's hold is not one of
  them. It is stranded until Phase 3's expiry worker, which does not exist yet.

The exception path has no such problem: a timeout or a 5xx raises `DownstreamServiceException`, and
that branch already reconciles by reading `GET /api/v1/reservations?orderId=`, which finds a hold
whose 201 nobody ever saw (O7). The hazard is specific to a retry that *succeeds in getting an
answer*, because then there is no exception and no read.

**Why confirm and release are retried.** Both are POSTs and both are safe, for the reason O9 and O10
already established: a 409 from either is never interpreted, it is resolved by the reconciliation
read, and `AllConfirmed` reports the lost-response case as the success it was.

**Why not the library's `DisableForUnsafeHttpMethods`.** It is the one-liner that removes the hazard,
and it was rejected because it disables retry for *every* POST — which is exactly the confirm and
release calls where retry is both safe and worth having. Marking the one unsafe request costs a line
in `ReserveAsync` and a predicate that reads it.

**The transient predicate is wrapped, not restated.** The retry option's own `ShouldHandle` decides
what transient means — 408, 429 and 5xx, plus `HttpRequestException` and `TimeoutRejectedException` —
and the exemption is `&&`-ed onto it. Restating the status list here would create a second definition
that could drift from the library's, and would risk retrying a normal 4xx.

**`HttpClient.Timeout` is deliberately not set.** The ten seconds O15 introduced is now the
pipeline's per-attempt timeout, unchanged in value. Setting `HttpClient.Timeout` as well would not be
a redundant safety net but a bug: it bounds the entire handler pipeline including retries, so a
ten-second value would abort the sequence before a second attempt could begin. Left unset it defaults
to 100 seconds, above the 35-second total timeout, and never fires first.

**Two new exception types had to be classified.** The pipeline reports its own timeouts as
`TimeoutRejectedException` rather than the `TaskCanceledException` `HttpClient` uses, and an open
circuit as `BrokenCircuitException`. Neither existed before, and both would have fallen through to
the handler's default arm and answered **500** — reporting a dependency that failed as a bug in this
service, the exact inversion the 502 arm exists to prevent. `DownstreamClient` now maps both to
`DownstreamServiceException`.

**Values.** Attempt timeout 10s (O15's bound) · total timeout 35s, sized for three attempts plus the
delays between them · two retries, constant 500ms rather than exponential, because the caller is
waiting synchronously and stock is held throughout · circuit breaker at 10 samples and a 50% failure
ratio, since Polly's default of 100 samples would mean the breaker never opens at this project's
traffic and a breaker that cannot trip is not one.

**What this does not do.** It does not make the contention 409 transparent. Stock's `xmin`-conflict
409 (D9) is a normal 4xx and is not retried, so the 40-way burst measurement in
`src/AgenticShop.Stock/docs/ARCHITECTURE.md` §7 is unchanged. Turning that into a transparent retry
needs either Stock to retry internally or its 409 causes to be distinguishable, and the shared error
contract forbids reading another service's `detail` to tell them apart (O9).

**Revisit.** Phase 2, when the outbox and saga replace synchronous placement — at which point the
outbound reserve is a published event with at-least-once delivery and an inbox, and per-request retry
exemption stops being the mechanism that keeps it safe.

---

## Not in the approval list, decided during implementation

- **Refusals are values and faults are exceptions** in both clients, so the placement path never has
  to inspect a status code to know whether the caller or a dependency is at fault. An unexpected 4xx
  from downstream is treated as *our* bug — `InvalidOperationException` → 500 — because answering 502
  would blame the other service for a contract we violated.
- **`DownstreamServiceException` never names the service in a response body.** The name is a typed
  property for the log line; disclosing dependencies to a caller discloses internal topology.
  `ADownstreamFailure_DoesNotNameTheServiceThatFailed` pins it, and
  `ADownstreamFailureStillLogsWhichServiceFailed` pins the other half.
- **`OrderLine` carries no timestamps.** It is write-once and part of an aggregate that has one clock.
  `ALineHasNoIdentityOrTimestampOfItsOwn` asserts the absence, so adding one is a decision rather
  than a drift.
- **`OrderResponse` sorts its lines.** An `Include` carries no ordering guarantee, and a response
  whose lines shuffle between two reads of the same order is harder to test and harder to read.
- **`CorrelationIdPropagatingHandler` was deleted rather than kept alongside tracing.** It forwarded
  `X-Correlation-Id` outbound; once the correlation id became the trace id, the HttpClient
  instrumentation injects `traceparent` and nothing reads the header it was sending. One lesson from it
  survives the code: `TryAddWithoutValidation` *appends* to a header that already exists rather than
  leaving it alone, so a `DelegatingHandler` — which sits in a pipeline it does not own — has to check
  before it adds, or a request built with its own value goes out with two.
- **Downstream base URLs are validated as absolute http/https at startup.** `Uri.TryCreate(...,
  UriKind.Absolute)` accepts `"localhost:5082"` — it reads `localhost` as the scheme — so the absolute
  check alone would pass a value that cannot reach anything. A test found this; the scheme check is the
  fix.
