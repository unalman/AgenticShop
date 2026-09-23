# Roadmap

Each phase adds one capability, and only once the previous phase has created the problem that
justifies it. The ordering is the point: **retrying without idempotency keys double-reserves
stock; an outbox without a broker to publish to is dead weight; CQRS without read pressure is
ceremony.**

`README.md` carries a short version of this list. This file is authoritative for detail.

Constraints on what may *not* be added early are in `AGENTS.md` §8; the reasoning behind each
deferral is in `DECISIONS.md`.

---

## Phase 0 — Foundation *(current, complete for Catalog)*

Three independently deployed hosts, synchronous HTTP between them, one database per service with
its own migrations and its own least-privilege PostgreSQL role, Testcontainers-backed integration
tests, PostgreSQL in compose.

**Done:** Catalog, end to end, as the reference implementation — domain, EF configuration, two
migrations, five endpoints, validation filter, exception handler, correlation id, soft delete,
paging, `xmin` concurrency, 138 passing tests, build-time and database-level boundary
enforcement.

**Not done:** Stock and Ordering do not exist. Their databases and roles are provisioned; there
is no project, no code and no client seam yet.

---

## Next: build out Stock and Ordering

Still Phase 0. The point is to prove the reference implementation replicates, and to create the
first real network hops.

Both follow Catalog's conventions, subject to the judgement calls in `AGENTS.md` §10 — soft
delete probably does not suit either, and `xmin` is far higher-stakes on stock than on a product.

**Stock** introduces the reservation lifecycle that the whole later roadmap depends on: hold
quantity against an order, then confirm or release it. `Available` is
`QuantityOnHand - Reserved`, computed rather than stored. The domain must reject a reservation
it cannot honour — that is business logic, not a copied guard clause.

**Ordering** introduces everything Catalog lacks:

- **A typed `HttpClient` plus an interface seam per downstream service.** The one abstraction
  justified in advance, because it is a real network boundary: the seam the integration tests
  fake, and the seam a broker-based implementation later swaps into.
- **Outbound correlation-id propagation** via a `DelegatingHandler`. Without it a trace breaks
  at the first hop.
- **Real nested request DTOs.** `CreateOrderRequest` with a `Lines` collection is the first
  genuine exercise of the validation cascade, which until now is proven only by synthetic
  contracts.
- **Order-line snapshotting.** `OrderLine` must copy `ProductName` and `UnitPrice` at the time of
  ordering, so a later catalog edit cannot rewrite order history. This is a standing project
  invariant, not a Catalog convention.
- **Compensation.** Placing an order reserves stock line by line; a failure part-way through must
  release whatever was already reserved.
- **A sequence or equivalent for `OrderNumber`** — which also means the test fixture must reset
  sequences, not just truncate rows (see `KNOWN-ISSUES.md`).

Expect the conventions to need revision here. Catalog is single-entity CRUD and exercises nothing
distributed. **When a convention proves wrong for the second service, fix it in Catalog too** —
divergent conventions across three services are worse than one imperfect convention applied
consistently.

This is also the point at which the shared-library trigger may fire early: `CatalogExceptionHandler`'s
chain-describing logic is already entirely generic, so a third copy is the signal, not Phase 1.

---

## Phase 1 — Reliability and observability

Now that a network hop exists, make its failure modes survivable and visible.

- Retry, circuit-breaker and timeout policies on the typed clients
- **Idempotency keys** on `POST /orders` and on reserve/confirm — a prerequisite for safe retries,
  which is why it precedes resilience rather than following it
- Serilog structured logging
- **OpenTelemetry** distributed tracing across the three hosts — the moment "distributed" stops
  being theoretical. Derive the correlation id from `Activity.Current?.TraceId` here, or there
  will be two parallel correlation concepts
- Dependency-aware health checks
- Dockerfiles for all three services plus a `full` compose profile
- CI: build, test, and `dotnet ef migrations has-pending-model-changes` as a drift gate

Also resolves, or forces a decision on, both open observability items in `KNOWN-ISSUES.md` — the
`Database.Command` Error entry for handled 409s, and the fact that validation rejections are
invisible.

---

## Phase 2 — Asynchronous messaging ← the inflection point

- RabbitMQ in compose
- Replace synchronous reservation with events: `OrderPlaced` → Stock consumes → `StockReserved` /
  `StockReservationRejected`
- **Outbox pattern** — only justified now, because it exists to make "write a row and publish an
  event" atomic. The design is already outbox-ready: every write path is a single
  `SaveChangesAsync`, so an entity and an outbox row would already be atomic
- **Inbox** for consumer-side deduplication. Prerequisite: the Phase 2 event contracts must carry
  a stable message id from the start — cheap to decide then, expensive to retrofit
- Saga / process manager in Ordering owning the order lifecycle and compensation
- Dead-letter queues, retry policies, at-least-once semantics

---

## Phase 3 — Data and scale

- Redis: catalog read caching plus a distributed idempotency store
- Reservation-expiry background worker (the `ExpiresAtUtc` concept deliberately skipped in Phase 0)
- Read models / projections — CQRS on the read side only, introduced when a real query needs it
- Consumer-driven contract tests (Pact), so services can evolve independently
- Keyset paging, if any table has grown enough to care

---

## Phase 4 — Platform

- YARP API gateway as the single entry point
- Kubernetes: manifests → Helm/Kustomize, ingress, config and secrets
- Service discovery and mTLS
- k6 load testing; failure and chaos injection

Note that Phase 4 is also where the Phase 0 trade-offs dissolve: one PostgreSQL container
becomes three, `container_name` and the fixed host ports stop mattering, and `AllowedHosts` gets
a real value.

---

## Phase 5 — Optional depth

Not committed to. Event sourcing on Ordering, multi-tenancy, progressive delivery.

Also: **bump to `net11.0`** once .NET 11 is GA (November 2026). It is a `TargetFramework` change
in `Directory.Build.props` plus package bumps — see `DECISIONS.md`.
