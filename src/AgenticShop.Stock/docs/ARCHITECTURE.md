# Architecture — Stock Service

Service-specific design detail. Global architecture — topology, boundary enforcement, the build
system, the shared service conventions and the testing architecture — lives in
`../../../docs/ARCHITECTURE.md`. Read that first; this file covers only what is specific to Stock.

The second service, and the test of whether Catalog's conventions transfer rather than merely
copy. Stock owns inventory: it holds quantity against an order, then settles that hold as either
shipped or cancelled.

## 1. Position in the topology — a leaf service

**Stock makes no outbound calls.** It has no `Clients/` folder, registers no `HttpClient` or
`IHttpClientFactory`, and never resolves a product against Catalog. Only Ordering orchestrates.

This is enforced at three levels: `Directory.Build.targets` rejects a compile-time reference;
`LeafServiceBoundaryTests` asserts at runtime that no `HttpClient`/`IHttpClientFactory` is
registered and that the assembly references no other `AgenticShop.*` assembly; and PostgreSQL
refuses `catalog_svc` a connection to `agenticshop_stock`.

Consequences accepted deliberately: Stock cannot tell you whether a product exists, so you *can*
provision stock for a nonexistent `ProductId`. That is tolerable because the only caller in the
real flow is Ordering, which validates against Catalog first. Stock also never learns a product's
name or price — snapshotting those is Ordering's job.

## 2. Domain

`StockItem` — the inventory counter, one row per product:

| Member | Notes |
|---|---|
| `ProductId` | **unique**; a plain `Guid`, because the product lives in Catalog's database |
| `QuantityOnHand` | physical count |
| `Reserved` | currently held, not yet shipped |
| `Available` | **computed** `QuantityOnHand - Reserved`; `builder.Ignore`d, never persisted |
| `MaxQuantity` | `1_000_000`, shared with the contracts so the counters cannot overflow `int` |

Methods `Create`, `SetQuantityOnHand`, `Reserve`, `ConfirmReservation`, `ReleaseReservation`, all
with guards before mutation. `SetQuantityOnHand` refuses to go below `Reserved`, or in-flight holds
would stop being coverable.

`StockReservation` — one row per hold: `StockItemId`, `OrderId`, `Quantity`, `Status`, timestamps,
plus a computed `IsPending`. No navigation property to `StockItem`; endpoints load both explicitly.

Two domain exceptions carry **typed properties rather than relying on `Message`**:
`InsufficientStockException(Available, Requested)` and
`InvalidReservationStateException(CurrentStatus, AttemptedStatus)`. The handler builds client-facing
text from those properties, so a reworded exception cannot silently change the public contract.

## 3. Why `Reserved` is a stored column

It could be derived as `SUM(quantity)` over pending reservations. It is stored instead because that
keeps the availability check and the mutation a **single-row operation guarded by one `xmin`
token**. Deriving it would require an aggregate read plus a write in one transaction, and
`StockItem`'s token would no longer cover reservation changes.

The reservation table is the *audit trail of intent*; `Reserved` is the *counter that makes the
check atomic*. Both are written by the **same `SaveChangesAsync`**, so they cannot disagree — and
that single-transaction property is precisely what Phase 2's outbox will rely on, since an outbox
row added to this context would commit atomically with both.

## 4. Reservation lifecycle

```
                 reserve(q)
   (no row) ─────────────────► PENDING
                                 │  │
                    confirm(q)   │  │   release(q)
                                 ▼  ▼
                           CONFIRMED  RELEASED      ← both terminal
```

| Transition | Effect on `StockItem` | Rationale |
|---|---|---|
| `reserve(q)` | `Reserved += q` | Holds the goods. `Available` drops immediately; `QuantityOnHand` is untouched |
| `confirm(q)` | `QuantityOnHand -= q`, `Reserved -= q` | Goods leave. `Available` is unchanged — it already fell at reserve time |
| `release(q)` | `Reserved -= q` | Order cancelled; hold lifted, `QuantityOnHand` untouched |

`Reserved <= QuantityOnHand` holds after all three, so `Available` can never go negative.

**Transitions are strict, not idempotent.** Confirming an already-`Confirmed` or `Released`
reservation throws rather than succeeding quietly, because a silent no-op would hide a
double-settle from the caller. The known cost: a retry after a successful confirm gets a 409.
Phase 2's at-least-once delivery will require idempotency — that is what Phase 1's idempotency
keys and the Inbox exist for. Until then **Ordering must treat "409 because already confirmed" as
success.**

There is no reservation expiry: no `ExpiresAtUtc`, no background worker. Release is an explicit
call only. The expiry worker is Phase 3.

## 5. Schema

```
stock_items
  id                uuid         PK
  product_id        uuid         NOT NULL, UNIQUE  (ix_stock_items_product_id)
  quantity_on_hand  integer      NOT NULL   CHECK >= 0
  reserved          integer      NOT NULL   CHECK >= 0, CHECK <= quantity_on_hand
  updated_at_utc    timestamptz  NOT NULL
  xmin              (system column — emits no DDL)

stock_reservations
  id              uuid         PK
  stock_item_id   uuid         NOT NULL  FK → stock_items(id) ON DELETE RESTRICT
  order_id        uuid         NOT NULL
  quantity        integer      NOT NULL   CHECK > 0
  status          varchar(20)  NOT NULL   CHECK IN ('Pending','Confirmed','Released')
  created_at_utc  timestamptz  NOT NULL
  updated_at_utc  timestamptz  NOT NULL
  xmin            (system column — emits no DDL)
  UNIQUE (order_id, stock_item_id)   ix_stock_reservations_order_id_stock_item_id
  INDEX  (order_id)                  ix_stock_reservations_order_id
```

Two indexes carry real weight:

- **`UNIQUE(order_id, stock_item_id)`** gives reserve natural idempotency, exactly as Catalog's
  unique SKU does. Because `stock_items.product_id` is itself unique, `stock_item_id` is 1:1 with
  `product_id` — so this enforces *one reservation per product per order* **without denormalising
  `ProductId` onto the reservation**. A side effect worth stating: Ordering must combine duplicate
  product lines.
- **`INDEX(order_id)`** supports listing and releasing every reservation for an order — the
  compensation path.

The foreign key is legitimate *because both tables are in Stock's own database*. The prohibition is
on keys crossing a service boundary, not on relational integrity within one.

Status is stored as a **string** with a `CHECK`, so `psql` shows `Pending` rather than `0`. Being
able to read reservation state directly matters once Phase 2 adds sagas. Cost: a
`HasConversion<string>()` and a `varchar(20)`.

Migration `20260923190112_InitialStock`, applied as `stock_svc`. Verified against the live
database: `information_schema.columns` returns **0 rows for `xmin`** on both tables, and all three
`stock_items` CHECK constraints are present in `pg_constraint`.

## 6. Endpoints

```
GET    /api/v1/stock/{productId}                       200 | 404
POST   /api/v1/stock                                   201 | 409 record exists | 400
PUT    /api/v1/stock/{productId}                       200 | 404 | 409 xmin | 400
POST   /api/v1/stock/{productId}/reservations          201 | 404 | 409 insufficient|duplicate | 400
POST   /api/v1/reservations/{id}/confirm               200 | 404 | 409 wrong state|xmin
POST   /api/v1/reservations/{id}/release               200 | 404 | 409 wrong state|xmin
GET    /api/v1/reservations?orderId=                   200 | 400 when orderId missing
GET    /health                                         200
```

There is **no list or paging endpoint for stock**. Catalog's paging convention is deliberately
unexercised here; adding it "for symmetry" would be exactly the speculative generality the project
avoids. `GET /reservations?orderId=` exists because it is cheap, indexed, and is the reconciliation
path for a half-failed order.

`orderId` is a required parameter, so omitting it is a `BadHttpRequestException` binding failure
→ 400 from the handler, not a 500 and not an unbounded scan.

## 7. Concurrency design

Both entities carry the token:

```csharp
builder.Property<uint>("xmin").IsConcurrencyToken().ValueGeneratedOnAddOrUpdate();
```

How it prevents overselling: two requests both read `xmin=5`, `Available=5`, and both try to
reserve 5. The first commits (`xmin` → 6). The second's `UPDATE ... WHERE id=@id AND xmin=5`
matches zero rows → `DbUpdateConcurrencyException` → **409**. Without the token both would succeed
and stock would go negative.

**There is no server-side retry on conflict.** Resilience libraries are Phase 1, and the 409 is the
honest signal that Ordering must learn to handle. It is also largely dissolved by Phase 2 — a queue
with a single consumer serialises reservations, so contention disappears rather than being retried
away.

The measured cost, from a live 40-way burst against 10 units:

```
201=4   409=36   5xx=0   other=0   total=40
reserved=4   quantity_on_hand=10   available=6
pending reservation rows=4   sum(quantity)=4
```

**Only 4 of 10 units were held.** Safety is perfect; liveness under burst is poor, because 36
requests lost the `xmin` race and were told 409 despite stock being available. A 20-way parallel
confirm of one reservation produced `200=1, 409=19, 5xx=0` and shipped exactly once.

This is the single most important input to the Ordering design: a synchronous order placement that
reserves line by line will fail often under load, and Ordering must decide whether to retry,
partial-fill, or fail the order.

## 8. Divergences from Catalog

Each was the right call for this service, and each is the evidence that the conventions transfer:

| Catalog | Stock | Why |
|---|---|---|
| soft delete + `HasQueryFilter` | **none** | a filtered stock row reads as "no stock", which is more dangerous than a deleted product |
| offset paging + `ProductPage` | **no list endpoint** | nothing needed it |
| `decimal` money + currency | **`int` counters only** | stock is not money |
| light concurrency test | **parallel overselling tests** | on `Product` a missed token loses a price edit; on `StockItem` it oversells |
| DTO-limit vs column-limit guard | **enum/CHECK and bound guards** | copying the original verbatim would have matched nothing and passed vacuously |

Copied verbatim, namespace only: `DataAnnotationValidationFilter`, `CorrelationIdMiddleware`,
`IRequestContract`, the test harness, and the error-handler skeleton.

## 9. Error classification specific to Stock

| Case | Status | Note |
|---|---|---|
| `InsufficientStockException` | 409 | detail built from `Available`/`Requested` |
| `InvalidReservationStateException` | 409 | detail names both states |
| `DbUpdateConcurrencyException` | 409 | Stock-specific wording, not Catalog's "product" |
| `23505` on `ix_stock_items_product_id` | 409 | "A stock record for that product already exists." |
| `23505` on `ix_stock_reservations_order_id_stock_item_id` | 409 | "That order already has a reservation for this product." |
| **`23514` CHECK violation** | **500** | deliberate — see below |

**The CHECK-violation mapping reverses the original Stock plan**, which called for a 4xx. Every
CHECK on these tables restates an invariant `StockItem` already guards, and `xmin` closes the
concurrent path, so a violation can only mean *our* code has a bug. Reporting it as 409 would hide
our own defect inside the caller's error budget. The invariant runs in both directions: a client
error is never a 5xx, **and a server fault is never a 4xx**. No handler arm was added; the default
arm already yields 500 at Error level, and `ACheckConstraintViolation_IsAServerFaultNotACallerError`
pins it so nobody "fixes" it later.

## 10. What Stock still does not exercise

Stock is a leaf service with no outbound calls, so it did **not** test: the typed `HttpClient`
seam, outbound correlation-id propagation, real nested request DTOs (the validation cascade is
still proven only by synthetic contracts), order-line snapshotting, compensation across services,
or a sequence-generated business number. Ordering will be the first service to exercise any of
them.

