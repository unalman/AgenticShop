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
| Downstream base URLs | `Services:Catalog:BaseUrl`, `Services:Stock:BaseUrl` |

## Rules specific to this service

- **Ordering is the orchestrator and the only service that makes outbound calls.** It calls Catalog
  to resolve and snapshot products and Stock to hold and settle them. It never joins, never shares a
  table, and never references either assembly — `Directory.Build.targets` and
  `ServiceBoundaryTests` both enforce that.
- **A consumer owns its own contract.** `CatalogProduct` and `StockReservationSnapshot` are declared
  here, with only the fields this service reads. Do not "complete" them to match the other service's
  response, and do not create a shared contracts project.
- **Never parse a downstream `detail`.** The shared error contract says a caller builds its own
  vocabulary from typed properties. Stock's 409 on confirm means either "already confirmed" (success,
  per decision D3) or "xmin conflict" (failure); the only way to tell them apart is
  `GET /api/v1/reservations?orderId=`, not the response text.
- **The order row is written exactly once, at the end, already terminal.** Never persist a `Pending`
  order before the downstream calls, and never add an "insert then update" path. `Pending` is the
  aggregate's construction state and stays in the enum for Phase 2's saga.
- **Never release a `Confirmed` reservation.** Stock refuses it, and even if it did not, release
  returns the hold without returning stock that has already left.
- **A reserve that fails after earlier lines were held → release every hold for that order and fail
  it.** No server-side retry in Phase 0, and no partial fill. Stock does not retry on `xmin`
  conflict, so a reserve can fail even when stock was available. See `docs/DECISIONS.md` → **O16**.
- **The typed clients retry, and reserve is the one call that must not.** `ReserveAsync` marks its
  request with `DownstreamResilience.NotRetryable`; do not remove that line, and do not add retry to
  a new outbound call without checking it is safe. A retried reserve whose first attempt committed is
  answered 409 by Stock's unique index, and the client reads 409 as a refusal — so the hold is
  stranded and the order is reported out of stock for stock that was taken. Reads and settles *are*
  retried, because their 409 is resolved by the reconciliation read instead of being interpreted.
  See `docs/DECISIONS.md` → **O18**.
- **`POST /orders` requires an `Idempotency-Key` header, and a repeated key replays.** The claim is
  written **after** resolve and **before** reserve — resolve is pure reads, so a bad basket must not
  burn the caller's key; reserve is the first side effect, so the claim must be committed first. The
  completion commits in the **same** `SaveChangesAsync` as the order. Keep it that way: moving the
  claim earlier wastes keys, moving it later reopens double-placement. See `docs/DECISIONS.md` → **O17**.
- **A confirm-phase failure ends as `PartiallyConfirmed`, not `Failed`.** `Failed` says nothing
  shipped, so a client that believes it re-orders — and the lines that were already confirmed ship a
  second time. The idempotency key stops an *accidental* retry; it cannot stop a client that
  deliberately chooses a new key because it was told nothing shipped. See `docs/DECISIONS.md` →
  **O14** and `docs/ARCHITECTURE.md` §5.
- **`PartiallyConfirmed` is reachable only from the confirm phase.** A reserve-phase fault cannot
  have shipped anything, so its order is `Failed` even when the reconciliation read also failed.
- **A 502 or 409 that wrote a row must echo the order id.** The handler puts it in a ProblemDetails
  extension; without it a client cannot tell "nothing was created" from "something was created and
  is broken".
- **502 for a dependency, 500 for ourselves.** An unexpected 4xx from a downstream service means this
  assembly sent something its contract does not allow, so it becomes an `InvalidOperationException`
  and a 500 — reporting it as a 502 would blame the other service for our bug.
- **Cancellation stops at the first side effect.** Resolving against Catalog honours the caller's
  token; from the first reserve onward everything uses `CancellationToken.None`, because a client
  that hangs up must not leave stock held against an order nobody recorded.
- **Reservation ids are never persisted.** Stock owns them. Reconciliation after the fact goes
  through `GET /api/v1/reservations?orderId=`, which reports the product id every line matches on.
- **Duplicate product lines are combined, not rejected.** Stock's `UNIQUE(order_id, stock_item_id)`
  allows one hold per product per order. Combining happens before resolution, so a repeated product
  costs one Catalog call.
- **One currency per order.** Enforced during resolution, before anything is reserved. A total summed
  across currencies is a meaningless number written into permanent order history.
- **`ProductName` and `UnitPrice` are snapshots, not references.** A later catalog edit must not
  rewrite order history. `ALineSnapshotsTheCatalogSoALaterEditCannotRewriteOrderHistory` pins it.
- **An illegal order transition is a 500, not a 409.** No caller input can reach it, so it can only
  mean this assembly transitioned twice. There is deliberately no handler arm for
  `InvalidOperationException`; the default arm is the correct answer, and a test pins it.
- **Money is `decimal` with a separate `Currency` string, rounded 2dp `AwayFromZero`** — the same
  rule Catalog applies to a price, so a snapshot cannot disagree with its source by a cent.
  `LineTotal` is computed and `builder.Ignore`d; `TotalAmount` is stored, because an order is
  write-once and the headline number must be readable without materialising the collection.
- **No list endpoint and no mutation endpoint.** `POST /api/v1/orders` and `GET /api/v1/orders/{id}`
  are the whole surface. Do not add paging "for symmetry with Catalog" or a cancel route "because
  orders have one"; neither has a Phase 0 caller.

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

**Ordering does not run alone.** A real placement needs Catalog on 5081 and Stock on 5082, plus the
compose database. The integration tests need neither, because both clients are faked at the seam.

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

Ordering's two deliberate divergences from the other services were reconciled into the shared
surface rather than forked: the `orderId` extension is data on `ExceptionClassification`, and the
502 arm lives in `ClassifyDomain` because "a dependency failed" is only expressible where a
dependency exists. Neither belongs in the other two services — do not back-port them.
