# AGENTS.md — Ordering Service

Read the repository-root `../../AGENTS.md` first. It holds the global rules: service and
database boundaries, technology, shared coding conventions, testing rules, configuration,
git rules and the list of things not to introduce. **This file only adds what is specific to
Ordering.**

Design detail: `docs/ARCHITECTURE.md` · decisions: `docs/DECISIONS.md` ·
open and resolved issues: `docs/KNOWN-ISSUES.md`.

## Identity

| | |
|---|---|
| Port | **5083** — fixed in `Properties/launchSettings.json` |
| Database | `agenticshop_ordering` |
| Role | `ordering_svc` (least privilege; never the `agenticshop` superuser) |
| Entities | `Order`, `OrderLine` |
| Connection string key | `ConnectionStrings:Ordering` |
| Downstream base URLs | `Services:Catalog:BaseUrl` |

## Rules specific to this service

- **Ordering is the orchestrator and the only service that makes outbound calls.** It calls Catalog
  over HTTP to resolve and snapshot products, and reaches Stock **only through the broker** —
  publishing `OrderPlaced` and consuming its outcomes. It never joins, never shares a table, and never
  references either assembly — `Directory.Build.targets` and `ServiceBoundaryTests` both enforce that.
  Do not re-add an `IStockClient`: there was one, and it was deleted with the synchronous path.
- **A consumer owns its own contract.** `CatalogProduct` and the records in `Messaging/` are declared
  here, with only the fields this service reads or writes. Do not "complete" them to match the other
  service's shape, and do not create a shared contracts project. The wire format is pinned from both
  sides independently — `StockEventContractTests` here, Stock's serializer test there.
- **The order row is written FIRST, as `Pending`, and settled later by a consumer.** This reverses the
  Phase 0 rule, which said the row was written last and already terminal. The row exists before the
  work does, so a crash after the save still leaves something to settle. Do not "fix" it back: an
  insert-then-update path is the design, not a smell.
- **The outbox row commits in the SAME `SaveChangesAsync` as the order and its lines.** Never split
  them. The single transaction is the whole point of the outbox — a row written after the order can be
  lost with the order already accepted, and a row written before can be published for an order that
  rolled back. Needing one transaction across several saves is a design smell to raise first.
- **`POST /orders` answers 202, not 201, and the body says `Pending`.** A client that treats 202 as
  success-and-done will believe an order shipped when it was only accepted. The `Location` header is
  the poll target.
- **The message id is the outbox row's id, not the order id.** Both sides' inboxes key on it. Reusing
  the order id would collide across an order's lifetime the moment anything republishes.
- **Never parse a downstream `detail`.** The shared error contract says a caller builds its own
  vocabulary from typed properties. Catalog answers 404 for an absent *and* a soft-deleted product, and
  `CatalogClient` turns that into `null` — which the placement path raises as
  `ProductUnavailableException`, not as a guess about why.
- **The typed client retries, and nothing is exempt.** The pipeline wraps the Catalog read, which is
  safe to retry because a read has no side effect. There *was* a per-request exemption for the
  synchronous reserve, and it is deleted with the call. **Do not add retry to a new outbound call
  without checking it is safe first** — a retried non-idempotent write is how a hold gets stranded and
  then reported as unavailable. Re-earn the exemption; do not assume it. See `docs/DECISIONS.md` →
  **O18**.
- **`POST /orders` requires an `Idempotency-Key` header, and a repeated key replays.** The claim is
  written **after** resolve and **before** the order — resolve is pure reads, so a bad basket must not
  burn the caller's key; the order write is the first side effect, so the claim must be committed
  first. The completion commits in the **same** `SaveChangesAsync` as the order and the outbox row.
  Keep it that way: moving the claim earlier wastes keys, moving it later reopens double-placement.
  See `docs/DECISIONS.md` → **O17**.
- **A replay derives its status from the order, it does not repeat a stored one.** 202 while the order
  is `Pending`, 200 once it has settled. Replaying the recorded code would answer 202 forever for an
  order that was confirmed minutes ago, and the client would keep polling finished work. What the key
  still guarantees exactly is the part that matters: a retry never places a second order.
- **`PartiallyConfirmed` cannot be written.** It stays in the enum and in the `CHECK` constraint
  because rows from before the flip carry it, and narrowing the vocabulary would mean rewriting them.
  Nothing produces a partial outcome now — Stock takes every line of an order or none. Do not restore
  a path to it. See `docs/ARCHITECTURE.md` §5.
- **No failure inside a request writes a row.** Catalog resolution is the only thing that can fail
  during a placement and it happens before the claim, so a 409 or 502 from `POST /orders` means
  nothing was created. That is why no response carries an `orderId` extension any more — there is no row
  to name. The mechanism stays in `ExceptionClassification`; a future path that writes a row and then
  fails must put the id back.
- **502 for a dependency, 500 for ourselves.** An unexpected 4xx from a downstream service means this
  assembly sent something its contract does not allow, so it becomes an `InvalidOperationException`
  and a 500 — reporting it as a 502 would blame the other service for our bug.
- **Cancellation stops at the first side effect.** Resolving against Catalog honours the caller's
  token; from the claim onward everything uses `CancellationToken.None`. The reason is the key, not
  stock: a claim committed with no order behind it refuses every later request under that key for good,
  so a caller who hangs up at the wrong moment loses the key rather than the order.
- **Duplicate product lines are combined, not rejected.** Stock's `UNIQUE(order_id, stock_item_id)`
  allows one hold per product per order. Combining happens before resolution, so a repeated product
  costs one Catalog call.
- **One currency per order.** Enforced during resolution, before anything is written. A total summed
  across currencies is a meaningless number written into permanent order history.
- **`ProductName` and `UnitPrice` are snapshots, not references.** A later catalog edit must not
  rewrite order history. `ALineSnapshotsTheCatalogSoALaterEditCannotRewriteOrderHistory` pins it.
- **An illegal order transition is a 500, not a 409.** No caller input can reach it, so it can only
  mean this assembly transitioned twice. There is deliberately no handler arm for
  `InvalidOperationException`; the default arm is the correct answer, and a test pins it.
- **Money is `decimal` with a separate `Currency` string, rounded 2dp `AwayFromZero`** — the same
  rule Catalog applies to a price, so a snapshot cannot disagree with its source by a cent.
  `LineTotal` is computed and `builder.Ignore`d; `TotalAmount` is stored, because the headline number
  must be readable without materialising the collection.
- **No list endpoint and no mutation endpoint.** `POST /api/v1/orders` and `GET /api/v1/orders/{id}`
  are the whole surface. Do not add paging "for symmetry with Catalog" or a cancel route "because
  orders have one". A cancel would need a compensation path for stock that has already shipped, and
  `Confirmed` is non-compensable on Stock's side.

## Commands

```powershell
dotnet ef migrations add <Name> -p src/AgenticShop.Ordering -s src/AgenticShop.Ordering --output-dir Data/Migrations
dotnet ef database update        -p src/AgenticShop.Ordering -s src/AgenticShop.Ordering
dotnet run --project src/AgenticShop.Ordering
dotnet test tests/AgenticShop.Ordering.UnitTests          # no Docker needed
dotnet test tests/AgenticShop.Ordering.IntegrationTests   # needs Docker
```

`--output-dir Data/Migrations` is not optional: the default is `Migrations/`, and the house
convention is per service under `Data/`.

**Ordering does not run alone.** A real placement needs Catalog on 5081, the compose database and a
reachable broker — but *not* Stock, because a placement no longer calls it: with the broker down the
order is still accepted as `Pending` and the outbox drains later. Stock on 5082 is what settles it. The
integration tests need none of them, because the Catalog client is faked at the seam and the two
messaging suites run their own broker container.

Manual requests: `../../http/ordering.http`, which drives all three services in order.
Scalar UI: <http://localhost:5083/scalar/v1> (Development only).

`dotnet run` always applies `launchSettings.json`, which pins
`ASPNETCORE_ENVIRONMENT=Development`. There is no `--environment` switch that overrides it, and
`--no-launch-profile` drops the environment *and* the port, so the service then fails fast on the
missing connection string. That failure is the configuration guard working, not a bug.

## Before changing a convention here

The validation filter, the correlation middleware, `IRequestContract` and the exception-handler base
class live in `src/AgenticShop.Shared/` — extracted at the start of Phase 1, so a change there
reaches all three services at once. Only the test harness is still per service. Which conventions are
shared: `../../docs/ARCHITECTURE.md` §4.

Ordering's divergence from the other services was reconciled into the shared surface rather than
forked: the 502 arm lives in `ClassifyDomain` because "a dependency failed" is only expressible where
a dependency exists. It belongs in no other service — do not back-port it. There was a second, the
`orderId` extension on a failure that wrote a row; nothing emits it now, because no failure inside a
request writes one. The mechanism stays on `ExceptionClassification` for the next path that does.
