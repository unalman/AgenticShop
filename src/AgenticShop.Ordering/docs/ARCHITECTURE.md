# Architecture — Ordering Service

Service-specific design detail. Global architecture — topology, boundary enforcement, the build
system, the shared service conventions and the testing architecture — lives in
`../../../docs/ARCHITECTURE.md`. Read that first; this file covers only what is specific to Ordering.

The third service, and the reason the other two exist separately. Ordering owns nothing a customer
can touch directly: no inventory, no product data. It owns the *decision* — that these lines, at
these prices, at this moment, are one order — and the coordination that turns that decision into
facts in two other databases.

## 1. Position in the topology — the orchestrator

**Ordering is the only service that makes an outbound call.** Catalog and Stock are both leaves: they
answer and forget. Ordering calls Catalog to resolve and snapshot products, then Stock to hold and
settle them.

```
              resolve + snapshot                hold, settle, read back
   Ordering ────────────────────► Catalog     ────────────────────────► Stock
      │                                                                 ▲
      └─────────────────────────────────────────────────────────────────┘
```

Neither leaf knows the other exists. Stock cannot tell you whether a product is real (it is a leaf,
by decision D4) and Catalog does not know it was ordered. Only Ordering holds both facts, and it
holds them for exactly as long as one request.

That asymmetry is why this service is where every distributed-systems problem in the roadmap first
becomes real: there is no transaction that spans the three databases, no broker to defer to, and no
retry that is safe without an idempotency key. What follows is the design that survives those
constraints in Phase 0 — and is explicit about where it does not.

## 2. Domain

`Order` — the aggregate root:

| Member | Notes |
|---|---|
| `Id` | **supplied by the caller of `Create`, not generated inside it** — Stock's reservations carry it and are created before the order row exists |
| `OrderNumber` | `ORD-yyyyMMdd-xxxxxxxx`, unique, derived from `Id` and the placement date |
| `Status` | `Pending` in memory only; persisted as `Confirmed`, `Failed` or `PartiallyConfirmed` |
| `Currency` | one per order, agreed across every line during resolution |
| `TotalAmount` | **stored**: the exact sum of already-rounded line totals |
| `PlacedAtUtc` | the only timestamp; there is no `UpdatedAtUtc` |
| `Lines` | `IReadOnlyCollection<OrderLine>` over a readonly backing field |
| `IsTerminal` | computed `Status != Pending`; `builder.Ignore`d |

`OrderLine` — write-once, no timestamps of its own:

| Member | Notes |
|---|---|
| `OrderId` | real foreign key, legitimate because both tables are in Ordering's own database |
| `ProductId` | Catalog's id, immutable, never resolved on read |
| `ProductName`, `UnitPrice` | **snapshots** taken at placement |
| `Quantity` | bounded by `OrderLine.MaxQuantity`, mirroring Stock's |
| `LineTotal` | **computed** `round(UnitPrice * Quantity)`, `builder.Ignore`d |

`TotalAmount` stored and `LineTotal` computed looks inconsistent and is not. `LineTotal` derives from
two columns of its own row, so it can never be read without them and cannot drift. `TotalAmount`
derives from a *collection*, so a computed version would silently read as zero whenever the lines
were not loaded — a wrong answer rather than an absent one. Storing it is safe because an order is
write-once: there is no path that changes a line after construction, so nothing can drift.

Four domain exceptions carry **typed properties rather than relying on `Message`**, so a reworded
exception cannot silently change the public contract: `ProductUnavailableException(ProductId)`,
`MixedCurrencyException(Currencies)`, `StockUnavailableException(ProductId, OrderId)`,
`OrderPlacementIncompleteException(OrderId, Status)`. A fifth, `DownstreamServiceException(Service,
Operation, StatusCode)`, lives in `Clients/` because it is a transport fact rather than a domain one.

An illegal order transition throws plain `InvalidOperationException` — no typed exception, and no
handler arm. Nothing a caller can do reaches it, so it can only mean this assembly transitioned
twice, and the default 500 arm is the honest answer. Same reasoning Stock applies to a CHECK
violation.

## 3. The placement flow

```
POST /api/v1/orders          (Idempotency-Key required)
  │
  ├─ replay check: is this key already completed?        → replay the recorded status, stop
  ├─ combine duplicate product lines                     (no I/O, no side effects)
  ├─ resolve every line against Catalog                  (throws → 400/409, key not consumed)
  │    └─ agree one currency across the lines
  ├─ build the Order in memory, Pending, OrderNumber derived from its id
  │
  ├─ CLAIM the idempotency key                           ◄── committed before any side effect
  │    └─ already claimed → 409, nothing placed
  │
  ├─ RESERVE each line                                   ◄── first side effect
  │    ├─ refused  → release the holds taken → Failed  → 409 + orderId
  │    └─ fault    → reconcile → Failed               → 502 + orderId
  │
  ├─ CONFIRM each reservation
  │    ├─ all settled → Confirmed                       → 201
  │    └─ otherwise   → reconcile → classify            → 201 | 502 + orderId
  │
  └─ one SaveChangesAsync writes the order, its lines and the key's completion, already terminal
```

Three properties of this ordering are load-bearing:

**Resolution completes before the first mutation anywhere.** An unorderable product or a
mixed-currency order therefore costs no compensation and writes no row. It is a 409 or a 400 with
nothing to clean up — and, since Phase 1, it does not consume the caller's idempotency key either,
so the basket can be fixed and retried under the same key.

**The claim sits between resolution and the first side effect.** Earlier and a caller's mistake burns
its key; later and two concurrent requests can both reach Reserve under the same key. Decision O17.

**The row is written once, at the end, already terminal.** There is no `Pending` row, so no reader
can observe a half-placed order, and there is no second write to fail between. The key's completion
rides in the same transaction, which is what closes the window a retry would otherwise exploit.
`Pending` remains in
the enum and in the `CHECK` because it is the construction state and because Phase 2's saga writes the
order *before* Stock replies — at which point a persisted `Pending` becomes the normal case and needs
the migration that introduces it.

The cost is stated plainly in §5: writing last means a process death mid-confirm leaves Stock holding
confirmed reservations for an order that was never written anywhere.

## 4. Cancellation stops at the first side effect

Resolving against Catalog honours the caller's `CancellationToken`: nothing has happened yet, so
abandoning is free. From the first reserve onward every call — reserve, confirm, release, the
reconciliation read and the final `SaveChangesAsync` — uses `CancellationToken.None`.

A client that hangs up mid-placement must not leave stock held against an order nobody recorded. The
calls are still bounded — by the resilience pipeline's timeouts (10s per attempt, 35s total, in
`Clients/DownstreamResilience.cs`) and by EF's command timeout — so this cannot hang a request. The
per-attempt timeout is the same ten seconds this section used to set through `HttpClient.Timeout`,
kept for the same reason: with no bound at all, "timed out" would never happen and the confirm-phase
policy would be unreachable in practice. Why `HttpClient.Timeout` is no longer set: decision O18.

## 5. Confirm-phase failure

Stock's `Confirmed` is terminal (decision D3) and confirm decrements `quantity_on_hand`. There is
therefore **no compensating action** for a confirm that succeeded: release is refused, and even if it
were not, it would return the hold without returning stock that has already left.

So when lines 1..k confirm and line k+1 fails or times out, the order is in a state none of the
original three statuses describes. `Failed` says nothing shipped — and `POST /orders` is not
idempotent, so a client that believes it re-orders and ships lines 1..k twice. `Confirmed` denies
that lines k+1..n never settled. `Pending` would never advance, because Phase 0 has no worker.

**The order becomes terminal `PartiallyConfirmed` and the response is 502.**

The classification is decided by a read, never by a status code and never by response text:

| Reconciliation read | Order status | HTTP |
|---|---|---|
| every line `Confirmed` | `Confirmed` — the confirm applied, only its response was lost | 201 |
| some but not all | `PartiallyConfirmed`, still-`Pending` holds released | 502 |
| none | `Failed`, every hold released | 502 |
| the read itself failed | `PartiallyConfirmed` — "unknown" is never recorded as "nothing happened" | 502 |

Releases target `Pending` rows only. A release that fails is logged and **does not change the
classification**: the status describes what shipped, and a stranded hold ships nothing. The hold
stays until Phase 3's expiry worker.

`PartiallyConfirmed` is reachable **only from the confirm phase**. A fault during reservation cannot
have shipped anything, so its order is `Failed` even when the reconciliation read also fails. That
asymmetry is what keeps the fourth status rare instead of routine.

The 502 carries the order id in a ProblemDetails extension next to `correlationId`. A 502 with no
identifying body leaves the client unable to tell "nothing was created" from "something was created
and is broken", and that ambiguity is what turns one failure into a duplicate shipment.

**Accepted residual.** If the *process* dies mid-confirm — a crash, not an HTTP failure — there is no
order row at all, because the row is written last, and Stock is left holding confirmed reservations
for an order id that no queryable surface can find. That is the dual-write problem; the Phase 2
outbox closes it. Tracked with its blast radius in `../../../docs/KNOWN-ISSUES.md` →
"Cross-service consistency", which is also where the "no path can oversell" argument lives.

## 6. The client seam

```
Clients/
  ICatalogClient / CatalogClient          GET  /api/v1/products/{id}
  IStockClient   / StockClient            POST /api/v1/stock/{productId}/reservations
                                          POST /api/v1/reservations/{id}/confirm
                                          POST /api/v1/reservations/{id}/release
                                          GET  /api/v1/reservations?orderId=
  CatalogProduct, StockReservationSnapshot    consumer-owned contracts
  CorrelationIdPropagatingHandler             outbound X-Correlation-Id
  DownstreamClient                            shared send + classify plumbing
  DownstreamServiceException
```

Three rules govern it:

- **Refusals are values, faults are exceptions.** A 404 or 409 comes back as `null` or `false`; a
  5xx, a timeout or a transport failure throws `DownstreamServiceException`. That split is what lets
  the placement path distinguish "the caller's order cannot be fulfilled" from "we were let down",
  without either becoming a 500.
- **An unexpected 4xx is our bug.** A 400 from Stock means Ordering sent something Stock's contract
  does not allow, so it becomes an `InvalidOperationException` → 500. Answering 502 would blame the
  other service for our defect.
- **Timeouts are caught explicitly.** `HttpClient` reports its own timeout as
  `TaskCanceledException`, and the exception handler already has an arm for that type — guarded on
  `RequestAborted`, so it stands down only when the *caller* hung up. Without the catch, a downstream
  timeout would produce no response at all.

Paths are rooted (`/api/v1/...`) rather than relative, so a base URL configured with a path prefix
cannot silently rewrite a route.

**Outbound correlation propagation was verified live, not only in tests.** The fakes replace the
clients, so no integration test can observe the header on the wire; `CorrelationIdPropagatingHandler`
is unit-tested in isolation instead. The wiring was then proven end to end by driving
`http/ordering.http` against all three hosts and finding Ordering's inbound id in *Stock's* log line:

```
Request rejected with 409 on POST /api/v1/stock/052d5e7e-…/reservations:
InsufficientStockException: Requested 100000 unit(s) but only 4 are available. | Correlation smoke-1643430592
```

That is the one property of this service that a faked suite cannot establish, so it is recorded here
rather than left as an assumption.

## 7. Schema

```
orders
  id             uuid          PK
  order_number   varchar(32)   NOT NULL, UNIQUE  (ix_orders_order_number)
  status         varchar(30)   NOT NULL   CHECK IN ('Pending','Confirmed','Failed','PartiallyConfirmed')
  currency       varchar(3)    NOT NULL
  total_amount   numeric(18,2) NOT NULL   CHECK >= 0
  placed_at_utc  timestamptz   NOT NULL
  xmin           (system column — emits no DDL)

order_lines
  id            uuid          PK
  order_id      uuid          NOT NULL   FK → orders(id) ON DELETE CASCADE  (fk_order_lines_orders)
  product_id    uuid          NOT NULL
  product_name  varchar(200)  NOT NULL
  quantity      integer       NOT NULL   CHECK > 0
  unit_price    numeric(18,2) NOT NULL   CHECK >= 0
  xmin          (system column — emits no DDL)
  UNIQUE (order_id, product_id)   ix_order_lines_order_id_product_id
  INDEX  (order_id)               ix_order_lines_order_id
```

Verified against the live database after applying `20260923223530_InitialOrdering` as `ordering_svc`:
`information_schema.columns` returns **0 rows for `xmin`** on both tables, and both CHECK constraints
are present in `pg_constraint`.

Three choices worth stating:

- **`UNIQUE(order_id, product_id)`** is the schema-level form of the one-line-per-product rule that
  Stock's `UNIQUE(order_id, stock_item_id)` forces on us. The placement path combines duplicates
  before it can be reached; the index is what makes forgetting to combine a loud failure rather than
  a refused reservation.
- **`ON DELETE CASCADE`**, where Stock's reservation → stock item key is `RESTRICT`. A line is part of
  the order aggregate and cannot outlive it; a reservation is an audit trail that must survive
  whatever happens to the counter it was taken against. Same question, opposite answers, and both are
  right.
- **`status varchar(30)`**, not 20 as Stock uses. `PartiallyConfirmed` is 18 characters, so 20 would
  fit and leave two characters of headroom for the next member. The alignment guard asserts the
  longest enum name fits, so widening costs nothing and a future member costs no migration.

`OrderNumber` needs no sequence. It is a pure function of the immutable id and the placement date, so
nothing has to be allocated, nothing can be skipped by a rolled-back transaction, and the test
fixture does not need to reset a sequence — which `ResetDatabaseAsync` cannot do, an open item in
`../../../docs/KNOWN-ISSUES.md` that this design simply does not create.

## 8. Endpoints

```
POST /api/v1/orders            201 | 400 | 409 | 502
GET  /api/v1/orders/{id}       200 | 404
GET  /health                   200
```

That is the whole surface. There is **no list endpoint**: a status-filtered list would be the
operator's way into `PartiallyConfirmed` orders, but it would need paging to be safe, and Catalog's
paging is explicitly not house style. Phase 0 does not need one — the 409 and 502 bodies carry the
order id and `GET /api/v1/orders/{id}` returns the whole record. Phase 2's saga needs a work queue,
which is a different thing.

There is **no route that mutates a persisted order** — no cancel, no confirm-later. That is what makes
the row write-once, `Pending` unpersisted, and `xmin` currently inert.

Response status and persisted status answer different questions, deliberately:

| Cause | HTTP | Persisted `Status` |
|---|---|---|
| every line reserved and confirmed | 201 | `Confirmed` |
| a product cannot be ordered, or currencies mix | 409 / 400 | no row written |
| Stock refused a line | 409 | `Failed` |
| a dependency failed; nothing confirmed | 502 | `Failed` |
| a dependency failed; something confirmed, or unknown | 502 | `PartiallyConfirmed` |

The HTTP status reports *why the call ended that way*; the row records *what happened to the order*.
Conflating them is what makes a retried placement ship twice.

## 9. Error classification specific to Ordering

| Case | Status | Note |
|---|---|---|
| `ProductUnavailableException` | 409 | covers absent and soft-deleted alike; Catalog's query filter answers 404 for both |
| `MixedCurrencyException` | 400 | `detail: null`, like every other domain 400; the clashing codes reach the log only |
| `StockUnavailableException` | 409 | `orderId` extension; the row was written as `Failed` |
| `OrderPlacementIncompleteException` | 502 | detail varies by status; `orderId` extension |
| `DownstreamServiceException` | 502 | no `orderId`: nothing was written. The service name stays in the log |
| `DbUpdateConcurrencyException` | 409 | arm present but **unreachable in Phase 0** — there is no update path |
| `23505` on `ix_orders_order_number` | 409 | "That order number already exists." |
| `23505` on `ix_order_lines_order_id_product_id` | 409 | "That order already has a line for this product." |
| `InvalidOperationException` | **500** | an illegal order transition; no arm, by design |
| **`23514` CHECK violation** | **500** | deliberate, as in Stock — see below |

Two of these need saying out loud.

`MixedCurrencyException` returns a 400 with `detail: null`, which reads as unhelpful and is
nonetheless correct — the reasoning, and the composed-detail alternative it rejected, is in
`DECISIONS.md` → **O8**. The clashing codes stay on the exception's typed property and reach the
operator through the log line; `AMixedCurrencyOrder_KeepsTheCurrenciesOutOfTheBodyAndInTheLog`
asserts both halves, so the property cannot be deleted as unused.

The CHECK-violation mapping is unchanged from Stock and follows global invariant two: a server fault
is never a 4xx. No arm was added; `ACheckConstraintViolation_IsAServerFaultNotACallerError` pins it,
alongside `AnIllegalOrderTransition_IsAServerFaultNotACallerError` for the transition case.

A 502 is logged at Error with the exception attached, so the fourteen integration tests that
deliberately provoke one produce fourteen Error lines. That is correct rather than noisy: a
dependency failing is what an operator pages on, unlike a routine 409.

## 10. Divergences from Catalog and Stock

Each was the right call for this service, and together they are the evidence that the conventions
transfer rather than merely copy:

| Catalog | Stock | Ordering | Why |
|---|---|---|---|
| soft delete + `HasQueryFilter` | none | **none** | an order must never vanish from a list |
| offset paging + `ProductPage` | no list endpoint | **no list endpoint** | nothing needed one; Phase 2 needs a queue, not a page |
| `decimal` money + currency | `int` counters only | **`decimal` money + one currency per order** | it prices an order, so it needs Catalog's money rule and Stock's counting rule at once |
| `CreatedAtUtc` + `UpdatedAtUtc` | both | **`PlacedAtUtc` only** | write-once; an `UpdatedAtUtc` that could only equal it would imply an update path that does not exist |
| `xmin` guards a live update path | `xmin` prevents overselling | **`xmin` declared but inert** | mandatory on every entity, and Phase 2's saga will need it; added then would mean a migration and a window without protection |
| handlers call the `DbContext` directly | same | **`OrderPlacer` collaborator** | the revisit `docs/DECISIONS.md` pre-authorised for "a use case that needs to coordinate an external call inside one transaction" |
| no navigation properties | none, flat graph | **`Order.Lines` is a real collection** | lines are inside the aggregate; Stock's reservation → stock item key crosses between two |
| 4xx/5xx only | 4xx/5xx only | **502 exists** | the first service with a dependency, so "we were let down" is a distinct fact from "we have a bug" |
| no outbound calls | leaf, asserted by test | **two typed clients**, asserted by test | the boundary guard inverts rather than disappears |
| structural guards re-derived | re-derived again | **re-derived again** | a copied guard that matches nothing passes vacuously |

Referenced from `AgenticShop.Shared`, not copied: `DataAnnotationValidationFilter`,
`CorrelationIdMiddleware`, `IRequestContract` and the `ProblemDetailsExceptionHandler` base class.
Only the test harness is still copied per service, because the integration assemblies must not
reference each other. Ordering's two divergences from the other services — the 502 arm and the
`orderId` extension — are reconciled into the shared surface rather than forked; see §9.

## 11. What Ordering still does not exercise

Ordering closed every gap Stock left open: the typed `HttpClient` seam, outbound correlation-id
propagation, real nested request DTOs, order-line snapshotting, and compensation across services.

Still unexercised, and named so nobody assumes otherwise:

- **A sequence-generated business number.** `OrderNumber` is derived, so the fixture's inability to
  reset sequences is still untested territory.
- **Real cross-service integration.** Every automated test fakes both clients. The three-host path is
  covered by `http/ordering.http` run by hand, not by CI. Consumer-driven contract tests are Phase 3.
- **Reconciliation as a process.** `PartiallyConfirmed` is recorded and queryable by id, but nothing
  drives it forward. Discovering one whose id was lost needs `psql`. Phase 2's saga.
- **Distributed tracing.** The correlation id crosses the hop and appears in both logs, but there is
  no span tree, so "which call was slow" is still a reading exercise. Phase 1's OpenTelemetry.

Idempotent placement was on this list until Phase 1 closed it; a stranded claim is the smaller
residual that replaced it, and it is in `KNOWN-ISSUES.md`. Resilience was on it too, and Phase 1
closed it as well — timeout, retry and a circuit breaker, with reserve exempt from retry; see
decision O18.
