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

The filter is byte-identical to Catalog's. Stock asserts only that it is *wired* to its endpoint
groups; the recursion behaviour is specified once, in Catalog's suite. Re-testing identical code
creates a second place to update and proves nothing.

## D13 · Stock gets its own `TestPostgreSql.Image` and compose guard

Otherwise the image-drift guard has a hole. The two integration assemblies must not reference each
other, so the constant and `ComposeConfigurationTests` are duplicated deliberately.

## D14 · Status stored as a string with a `CHECK`, not an integer

So reservation state is readable in `psql`, which matters once Phase 2 adds sagas and
reconciliation. Cost: `HasConversion<string>()` plus a `varchar(20)`. A new enum member without a
matching CHECK update fails loudly at insert time — the intended behaviour, since the schema and
the enum are one vocabulary. `ContractSchemaAlignmentTests` asserts the two lists agree.

## Not in the approval list, decided during implementation

- **Domain exceptions carry typed properties**, and the handler builds client text from them rather
  than from `Message`. Prevents a reworded exception from silently changing the public contract.
- **A diverged-counter fault is a 500.** `StockItem.RequireCoveredByReserved` throws
  `InvalidOperationException` when `Reserved` has drifted from the reservation rows. No caller
  input can produce it, so blaming the caller would be wrong.
- **`StockApiFixture.DisposeAsync` uses `try/finally`**, unlike Catalog's. Fixed forward rather
  than copied; the divergence is tracked in `../../../docs/KNOWN-ISSUES.md`.

