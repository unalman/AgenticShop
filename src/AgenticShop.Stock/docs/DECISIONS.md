# Decisions — Stock Service

Why Stock is built the way it is. Global decisions — platform, topology, boundaries, the shared
error contract, validation, testing strategy, security — live in `../../../docs/DECISIONS.md`
and are not repeated here.

D1–D14 were approved before implementation; the numbering matches that approval. The final
section records three decisions taken during implementation. Design detail lives in
`ARCHITECTURE.md` alongside this file.

## D1 · `Reserved` is a stored column, not derived from reservations

**Alternative rejected:** `SUM(quantity)` over pending reservations.

**Why stored.** It keeps the availability check and the mutation a single-row operation guarded by
one `xmin` token. Deriving it would need an aggregate read plus a write in one transaction, and
`StockItem`'s token would no longer cover reservation changes. The reservation table is the audit
trail; `Reserved` is the counter that makes the check atomic. Both are written by the same
`SaveChangesAsync`, which preserves the outbox-readiness Phase 2 depends on.

## D2 · `UNIQUE(order_id, stock_item_id)`

Gives reserve natural idempotency, exactly as Catalog's unique SKU does — a retry conflicts
instead of double-holding. Because `stock_items.product_id` is itself unique, `stock_item_id` is
1:1 with `product_id`, so this enforces one reservation per product per order **without
denormalising `ProductId` onto the reservation**.

**Consequence accepted:** Ordering must combine duplicate product lines in an order.

## D3 · Transitions are strict, not idempotent

Confirming an already-settled reservation throws rather than returning success.

**Why.** A silent no-op would hide a double-settle from the caller.

**Known cost.** A retry after a successful confirm gets a 409. Phase 2's at-least-once delivery
requires idempotency, which is what Phase 1's idempotency keys and the Inbox exist for. Until then
**Ordering must treat "409 because already confirmed" as success.**

**Second consequence, found while planning Ordering.** Strictness plus a terminal `Confirmed` means
there is no compensating action for a confirm that already succeeded. `release` on a `Confirmed`
reservation throws — and even if it did not, it would return `Reserved` without returning
`QuantityOnHand`, leaving the counters describing stock that has already left. An order whose
confirms fail part-way therefore cannot be rolled back, only recorded. What Ordering records is its
decision: `../../AgenticShop.Ordering/docs/DECISIONS.md` → **O14**.

**Revisit:** Phase 1/2, alongside idempotency keys.

## D4 · Stock does not call Catalog — it is a leaf service

Only Ordering orchestrates. If Stock also called Catalog it would add a hop, a Stock→Catalog
coupling and a trace path that nothing in Phase 0 needs.

**Consequence accepted:** Stock cannot tell you whether a product exists, so stock can be
provisioned for a nonexistent `ProductId`. Tolerable because the only caller in the real flow is
Ordering, which validates first. Enforced by `LeafServiceBoundaryTests`.

## D5 · `CHECK` constraints on `stock_items` — **with a reversal**

The constraints were approved as defence-in-depth, and the plan said to map SQLSTATE `23514` to a
client error so a violation would not surface as a 500.

**The mapping was reversed during implementation.** Every CHECK restates an invariant `StockItem`
already guards, and `xmin` closes the concurrent path, so a violation can only mean *our* code has
a bug. Reporting it as 4xx would hide our defect inside the caller's error budget. The invariant
runs in both directions: a client error is never a 5xx, **and a server fault is never a 4xx**.

No handler arm was added — the default arm already yields 500 at Error level.
`ACheckConstraintViolation_IsAServerFaultNotACallerError` pins it so nobody "fixes" it later.

## D6 · Real foreign key `stock_reservations.stock_item_id → stock_items.id`

Both tables are in Stock's own database, so the key is legitimate; the prohibition is on keys
*crossing* a service boundary. `DeleteBehavior.Restrict` with `WithMany()` — no navigation
property, so the object graph stays flat and there is no cascade behaviour to reason about.

**Consequence for tests:** `ResetDatabaseAsync` must delete reservations before stock items.

## D7 · `POST /stock` plus `PUT /stock/{productId}`, not a single upsert

Explicit create-versus-update mirrors Catalog, and a silent upsert would hide a
duplicate-provisioning bug. The unique index on `product_id` makes the race safe.

## D8 · `/stock/{productId}/reservations`, not `/stock/{productId}/reserve`

Resource-oriented, matching `POST /api/v1/products`.

## D9 · No server-side retry on `xmin` conflict — deferred to Phase 1

**Why.** Resilience libraries are Phase 1, and the 409 is the honest signal Ordering must learn to
handle. Phase 2 largely dissolves the problem: a queue with a single consumer serialises
reservations, so contention disappears rather than being retried away.

**Measured cost.** Poor liveness under burst, quantified in `ARCHITECTURE.md` §7 — the canonical
record. This was the most important input to the Ordering design, which decided to **fail fast**
with no server-side retry in Phase 0: `../../AgenticShop.Ordering/docs/DECISIONS.md` → **O16**.

## D10 · `GET /api/v1/reservations?orderId=` is included

Cheap, supported by the `order_id` index, and needed for Phase 2 reconciliation and for debugging a
half-failed order. `orderId` is required, so omitting it is a binding failure → 400, not an
unbounded scan.

## D11 · A test asserts Stock registers no `HttpClient`

`LeafServiceBoundaryTests` locks in the leaf-service boundary at runtime, covering what
`Directory.Build.targets` cannot — a dependency introduced through DI rather than a project
reference.

## D12 · Do not duplicate the validation filter's recursion tests

The filter is shared code — it lives in `AgenticShop.Shared` and is specified once, in
`AgenticShop.Shared.UnitTests`. Stock asserts only that it is *wired* to its own endpoint groups.
Re-testing shared code creates a second place to update and proves nothing.

## D13 · Stock gets its own `TestPostgreSql.Image` and compose guard

Otherwise the image-drift guard has a hole. The two integration assemblies must not reference each
other, so the constant and `ComposeConfigurationTests` are duplicated deliberately.

## D14 · Status stored as a string with a `CHECK`, not an integer

So reservation state is readable in `psql`, which matters once Phase 2 adds sagas and
reconciliation. Cost: `HasConversion<string>()` plus a `varchar(20)`. A new enum member without a
matching CHECK update fails loudly at insert time — the intended behaviour, since the schema and
the enum are one vocabulary. `ContractSchemaAlignmentTests` asserts the two lists agree.

## D15 · The settle path reads the reservation and its stock item in one statement

`SettleAsync` needs two rows. Reading them in two queries gave each its own READ COMMITTED
snapshot, so a winner committing in between left the loser holding a stale `Pending` reservation
next to a fresh `Reserved = 0`: the state machine passed on the stale half,
`RequireCoveredByReserved` threw on the fresh half, and because it throws *before*
`SaveChangesAsync` the `xmin` token never ran. An ordinary conflict reached the caller as a 500.

Measured on a live container: **7 × 500 in 10,000 parallel confirms, 6 × 500 in 10,000 parallel
releases**, with the counters correct in every iteration. Reserve was immune — 0 in 25,000 — because
it reads one row and `InsufficientStockException` is already classified as a 409. That asymmetry is
what identifies the two-statement read as the cause rather than something environmental.

One LEFT JOIN means one snapshot, so the loser gets either the state machine's 409 or the `xmin` 409
and there is no third outcome. Re-measured after the fix: **0 × 500 across 45,000 requests**.

**LEFT, not inner.** The RESTRICT foreign key makes a missing stock item unreachable, and that case
is deliberately a 500 rather than the caller's 404. An inner join would collapse the two and report
data damage as a bad request.

**Why a statement-count guard instead of a bigger burst.** At 7 in 10,000, a ten-request parallel
test sees this about once in a hundred runs, so five consecutive green full-solution runs are the
*expected* outcome and not evidence of absence. `SettleReadAtomicityTests` therefore counts SQL:
exactly one SELECT naming both tables, asserted for confirm and release. It pins the mechanism
rather than racing it, and carries a non-vacuity assertion — which earned its keep immediately,
because the first wiring registered the interceptor in DI where EF Core did not pick it up, so the
capture recorded nothing and would otherwise have asserted on an empty list.

Full account: `../../../docs/KNOWN-ISSUES.md` → "The 'unexplained 5xx' was a read-skew race".

## D16 · The consumer, the inbox, and why neither is registered yet

**Added 2026-10-23, Phase 2b-i.**

**Decision.** `OrderPlacedConsumer` reads `OrderPlaced` from a durable queue Stock owns, holds every
line of the order or none of them, and publishes `StockCommitted` or `StockReservationRejected` through
an outbox of its own. An `InboxMessage` row keyed on the producer's message id makes redelivery a
no-op. **Neither this consumer nor Stock's publisher is registered in `Program.cs`.** The test host
registers both; production does not.

**Why built but not wired.** The synchronous `POST /api/v1/stock/{productId}/reservations` path is
still the only one Ordering uses. Registering the consumer now would put two reservation paths live at
once and double-hold every order. Building it unwired is what lets it be tested against a real broker
without changing a single shipped behaviour — all 68 pre-existing Stock integration tests pass
untouched, and `POST /orders` still answers exactly as it did. The increment that registers this also
deletes the synchronous reserve and moves Ordering's emission point, in one commit.

**Why the inbox, when `UNIQUE(order_id, stock_item_id)` already looks like deduplication.** It covers
a different case, and the gap is not hypothetical — `ARejectedMessageStaysRejectedAfterStockArrives`
reproduces it. A *refused* hold writes no reservation row, so a redelivery has nothing to collide
with; if stock arrived in between, the redelivery succeeds and publishes `StockCommitted` for an order
already answered with `StockReservationRejected`. Two contradictory outcomes for one message id. The
unique index also cannot say whether the outcome event was published, only that a hold exists. The
inbox row is written in the same transaction as the hold and the outbox row, which makes "processed"
and "answered" one fact instead of two. The unique index stays and stays load-bearing for the HTTP
path's 409.

**Why all-or-nothing per order.** The synchronous path reserves line by line and releases the earlier
holds when a later one fails. Here the whole order is one unit of work, so there is no intermediate
state to compensate for — which is why the rejection path can simply discard its `DbContext` and write
the refusal in a fresh one rather than undoing mutations. `OneUnfulfillableLineRollsBackTheWholeOrder`
pins that the first line's successful hold never reaches the database.

**Why it reserves but does not confirm.** A `Confirmed` reservation is terminal (D3) and Ordering is
forbidden from releasing one (its O10). Confirming here would discard the only compensation the saga
has. The holds stay `Pending`, and confirming them is a later increment's decision.

**Why the outbox is a copy rather than shared infrastructure.** Extracting it into
`AgenticShop.Shared` was considered and declined: it would widen what that assembly is permitted to
hold, and freezing the shape before a second consumer existed is the mistake Shared's own deferral
entry names. The project owner chose duplication plus a guard. `OutboxPublisherSafetyTests` asserts the
safety properties against Stock's copy; Ordering's suite should assert the same list against its own,
and currently asserts less — recorded in `../../../docs/KNOWN-ISSUES.md`.

**Why `prefetchCount: 1` and an explicit `consumerDispatchConcurrency: 1`.** Serial dispatch actually
comes from the client's default dispatch concurrency, not from prefetch; prefetch bounds how many
unacked deliveries the broker hands over. Both are set explicitly because this consumer mutates a row
other writers also mutate, so its correctness should not rest on two library defaults nobody pinned —
and the channel-level option does not inherit the factory value unless it is passed as `null`.

**Why the queue is durable.** RabbitMQ 4 rejects a transient non-exclusive queue outright
(`code=541, "Feature 'transient_nonexcl_queues' is deprecated"`), found by a failing test rather than
by documentation. Exclusive is not available to a consumer that must survive a restart.

**Why a poison message is logged and acknowledged.** There is no dead-letter queue — `AGENTS.md` §8
defers them. Nack without a DLQ discards the message and leaves nothing to inspect; requeue loops
forever filling the log. So the body is logged at `Error` and the delivery is acknowledged. The log
line is the only surviving copy, which is a real loss and is recorded rather than papered over.

**Verified.** Full suite **545/545**, of which Stock's integration project is 77 (68 before this
decision, plus one broker-image guard and eight messaging tests). The messaging tests run against a
real `rabbitmq:4-alpine` container and assert the round trip end to end: a published `OrderPlaced`
produces a `Pending` reservation, the counters move, an inbox row appears, and a `StockCommitted`
arrives on a queue bound to `stock.#` carrying the producer's `traceparent` across both hops.
Redelivery of one message id holds once. A refusal followed by restocking and redelivery still holds
nothing.

**What it does not do.** No saga, no confirmation of the holds, no dead-letter queue, no retry policy
on the message, and no change to any HTTP endpoint. Ordering still reserves synchronously and still
publishes `OrderPlaced` only for `Confirmed` orders.

**Revisit.** The very next increment, which registers both hosted services, deletes the synchronous
reserve, moves Ordering's emission to before reservation, and turns `POST /orders` into a 202 with a
`Pending` order.

### Revised 2026-10-23, the same day — it now commits, and the event was renamed

Two things in this entry were superseded within hours of being written, both by the same realisation.

**"Why it reserves but does not confirm" is no longer the design.** That paragraph argued that
confirming early would discard the saga's only compensation, because a `Confirmed` reservation is
terminal (D3) and Ordering may not release one (its O10). The argument was sound but it was answering a
question this design does not have: the two-phase split existed so Ordering could undo a *partial*
hold, and an all-or-nothing hold has no partial state to undo. With every line taken or none taken in
one transaction, there is nothing left to compensate for, so the consumer reserves and confirms
together and the units leave `quantity_on_hand` before the outcome event exists.

The cost is stated rather than hidden: if cancellation ever arrives there is nothing to release.
`StockItem.ReleaseReservation` and the `Released` status are kept for that conversation — which makes
them unreachable from this path, the same shape `Order.Pending` had in Phase 0.

**`StockReserved` became `StockCommitted`, and `stock.reserved` became `stock.committed`.** Not
cosmetics: a consumer reading "reserved" would reasonably assume the units were still held and could
be given back, and would design its own compensation around a guarantee Stock no longer offers.
Renaming was free here and would not have been later, which is the same argument `docs/ROADMAP.md`
makes about deciding a message id early. Note the deliberate vocabulary split — the table stays
`stock_reservations` and the entity stays `StockReservation`, because that is the audit record, while
the event is named for what happened to inventory.

So the "Verified" paragraph above is historical: the counters it describes moving now move further
(`quantity_on_hand` drops, `reserved` returns to zero), the reservation it calls `Pending` is written
`Confirmed`, and the suite has grown since. The current numbers are in the increment that made the
change.

### Revised again 2026-10-23, Phase 2b-iii — it is wired now

**Both hosted services are registered in `Program.cs`, and the synchronous reservation path they
replaced is deleted from Ordering.** The "Why built but not wired" paragraph above is historical: it
described a real constraint (two live reservation paths would double-hold every order) that the flip
resolved by removing one of them in the same commit that registered the other. Stock's own HTTP
reservation endpoints were *not* deleted in that commit — they still exist and now have no caller, and
removing them with the ~30 tests that cover them is a separate cleanup, so that the behavioural change
stays reviewable on its own.

Two consequences worth carrying forward from the wiring:

- **`mandatory` is now `true` on Stock's publisher**, because Ordering binds `ordering.stock-outcomes`.
  It had to flip in the same commit as Ordering's own flag, which is exactly the forgetting risk
  `docs/KNOWN-ISSUES.md` recorded when both were `false`.
- **Stock's startup now requires a broker URL.** `Program.cs` validates `RabbitMq:Url` the way it
  validates the connection string, so a missing value fails the boot rather than producing a warning
  from two background loops every five seconds. An *unreachable* broker does not fail startup, and
  should not: unacknowledged deliveries are redelivered and unpublished rows wait.

## Not in the approval list, decided during implementation

- **Domain exceptions carry typed properties**, and the handler builds client text from them rather
  than from `Message`. Prevents a reworded exception from silently changing the public contract.
- **A diverged-counter fault is a 500.** `StockItem.RequireCoveredByReserved` throws
  `InvalidOperationException` when `Reserved` has drifted from the reservation rows. No caller
  input can produce it, so blaming the caller would be wrong. It *was* reachable with no bad input
  at all, through the read skew D15 removed — which is exactly why the classification stays at 500
  rather than being softened to a 409: with the skew closed, reaching it again means real corruption.
- **`StockApiFixture.DisposeAsync` uses `try/finally`**, unlike Catalog's. Fixed forward rather
  than copied; the divergence is tracked in `../../../docs/KNOWN-ISSUES.md`.

