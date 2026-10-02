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

- **`Failed`.** Says nothing shipped. `POST /orders` is not idempotent, so a client that believes it
  re-orders and ships the confirmed lines a second time. This is the one genuinely dangerous answer
  available, and a three-status vocabulary forces it.
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

**Why.** `POST /orders` is not idempotent in Phase 0. A failure body that names no order leaves the
client unable to distinguish "nothing was created" from "something was created and is broken", and
that ambiguity is precisely what turns one failure into a duplicate shipment. The extension costs one
line in the handler and is the difference between an actionable failure and a guess.

**Cost accepted.** `Classify` returns a small record rather than the copied skeleton's tuple. That is
a divergence from the shared handler, recorded in `ARCHITECTURE.md` §9 and to be folded into the
Phase 1 extraction rather than back-ported now, since neither Catalog nor Stock has an order to name.

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

- **A fourth status, not a reuse of the other three.** `Failed` says nothing shipped, and
  `POST /orders` is not idempotent, so a client that believes it re-orders and ships the confirmed
  lines a second time — the one genuinely dangerous answer available, and the one a three-status
  vocabulary forces. `Pending` would never advance, because Phase 0 has no worker. A
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
recorded. The forward calls are still bounded — by `HttpClient.Timeout` and EF's command timeout — so
this cannot hang a request.

**Why the timeout is set at all.** Ten seconds, and it is not a resilience policy: there is no retry
and Polly is Phase 1. It exists so that "timed out" can happen. With the 100-second default the whole
confirm-phase policy would be unreachable in practice, and the failure mode O14 exists for would never
arrive.

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

**Revisit.** Phase 1, once idempotency keys make a retry safe.

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
- **`CorrelationIdPropagatingHandler` checks before it adds.** `TryAddWithoutValidation` *appends* to
  a header that already exists rather than leaving it alone, so without the check a request built with
  its own value would go out with two. A `DelegatingHandler` sits in a pipeline it does not own.
- **Downstream base URLs are validated as absolute http/https at startup.** `Uri.TryCreate(...,
  UriKind.Absolute)` accepts `"localhost:5082"` — it reads `localhost` as the scheme — so the absolute
  check alone would pass a value that cannot reach anything. A test found this; the scheme check is the
  fix.
