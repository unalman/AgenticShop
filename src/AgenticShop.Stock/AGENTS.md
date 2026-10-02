# AGENTS.md — Stock Service

Read the repository-root `../../AGENTS.md` first. It holds the global rules: service and
database boundaries, technology, shared coding conventions, testing rules, configuration,
git rules and the list of things not to introduce. **This file only adds what is specific to
Stock.**

Design detail: `docs/ARCHITECTURE.md` · decisions: `docs/DECISIONS.md` ·
open and resolved issues: `docs/KNOWN-ISSUES.md`.

## Identity

| | |
|---|---|
| Port | **5082** — fixed in `Properties/launchSettings.json` |
| Database | `agenticshop_stock` |
| Role | `stock_svc` (least privilege; never the `agenticshop` superuser) |
| Entities | `StockItem`, `StockReservation` |
| Connection string key | `ConnectionStrings:Stock` |

## Rules specific to this service

- **Stock is a leaf service. It makes no outbound calls.** No `Clients/` folder, no `HttpClient`,
  no `IHttpClientFactory`, no reference to Catalog. `LeafServiceBoundaryTests` fails if any of
  that appears. Only Ordering orchestrates; if Stock started calling Catalog it would add a hop
  and a coupling nothing in Phase 0 needs.
- **Consequence to accept:** Stock cannot tell you whether a product exists. Do not "fix" that by
  adding a Catalog call.
- **No soft delete, no query filter, no `includeInactive`.** A silently filtered stock row reads
  as "no stock", which is more dangerous than a deleted product. Catalog's soft delete is *not*
  house style.
- **No list or paging endpoint.** Nothing needs one; symmetry with Catalog is not a reason.
  `GET /api/v1/reservations?orderId=` exists as the reconciliation and compensation path.
- **Quantities are `int`, never money.** Do not add `decimal`, currency or precision here.
- `Available` is a **computed property** (`QuantityOnHand - Reserved`), `builder.Ignore`d. Never
  store it — a second copy of the truth will drift.
- The invariant is **`0 <= Reserved <= QuantityOnHand`**, so `Available` can never go negative.
  Every mutation must preserve it, and `SetQuantityOnHand` must refuse to go below `Reserved`.
- **`xmin` is on both entities and is not optional.** On `StockItem` a missed token is
  overselling, not a lost edit.
- **Counter and reservation row commit in one `SaveChangesAsync`.** Never split them; the single
  transaction is what keeps them from disagreeing, and it is what Phase 2's outbox will rely on.
- **Reservation transitions are strict, not idempotent.** `Pending → Confirmed | Released`, both
  terminal. An illegal transition throws `InvalidReservationStateException` → 409. Do not make
  confirm or release silently succeed on an already-settled reservation; that would hide a
  double-settle. Callers must treat "409 already confirmed" as success.
- **Domain conflicts get their own exception type with typed properties** —
  `InsufficientStockException(Available, Requested)`. `ArgumentException` maps to 400 and cannot
  express a conflict.
- **`UNIQUE(order_id, stock_item_id)` means one hold per product per order.** Let the index reject
  duplicates; do not pre-check with a `SELECT`.
- **A `CHECK` violation is a 500, not a 4xx.** Every CHECK restates an invariant the entity
  already guards, so a violation means our code has a bug. Do not add a handler arm for `23514`.
- **Any change to a counter needs a parallel test.** Sequential state-machine tests prove almost
  nothing here — see `docs/ARCHITECTURE.md` §7 and `../../../docs/ARCHITECTURE.md` §5.3. Parallel
  assertions are inequalities (never oversold, no 5xx, counters agree with committed rows), never
  exact success counts.
- `ResetDatabaseAsync` must delete `stock_reservations` **before** `stock_items`; the foreign key
  is `RESTRICT`.

## Commands

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet ef migrations add <Name> -p src/AgenticShop.Stock -s src/AgenticShop.Stock
dotnet ef database update       -p src/AgenticShop.Stock -s src/AgenticShop.Stock
dotnet run --project src/AgenticShop.Stock
dotnet test tests/AgenticShop.Stock.UnitTests          # no Docker needed
dotnet test tests/AgenticShop.Stock.IntegrationTests   # needs Docker
```

Manual requests: `../../http/stock.http`. Scalar UI: <http://localhost:5082/scalar/v1>
(Development only).

Stock runs independently — it needs no other service up.

## Before changing a convention here

The validation filter, the correlation middleware, `IRequestContract` and the exception-handler base
class are no longer copied — they live in `src/AgenticShop.Shared/`, so a change there reaches all
three services at once. Only the test harness is still per service. Stock's handler supplies just its
two domain arms and its two message strings; everything else is inherited. Which conventions are
shared: `../../docs/ARCHITECTURE.md` §4.
