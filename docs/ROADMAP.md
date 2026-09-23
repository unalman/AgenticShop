# Roadmap

Each phase adds one capability, and only once the previous phase has created the problem that
justifies it. The ordering is the point: **retrying without idempotency keys double-reserves
stock; an outbox without a broker to publish to is dead weight; CQRS without read pressure is
ceremony.**

`README.md` carries a short version of this list. This file is authoritative for detail.

Constraints on what may *not* be added early are in `AGENTS.md` §8; the reasoning behind each
deferral is in `DECISIONS.md`.

---

## Phase 0 — Foundation *(current: Catalog and Stock complete, Ordering outstanding)*

Three independently deployed hosts, synchronous HTTP between them, one database per service with
its own migrations and its own least-privilege PostgreSQL role, Testcontainers-backed integration
tests, PostgreSQL in compose.

**Catalog — done.** The reference implementation: `Product` domain, EF configuration, two
migrations, five endpoints, validation filter, exception handler, correlation id, soft delete,
paging, `xmin` concurrency. 138 tests.

**Stock — done.** The second service and the proof that the conventions transfer rather than merely
copy: `StockItem` + `StockReservation` domains, one migration, seven endpoints plus `/health`, the
reservation lifecycle (`Pending → Confirmed | Released`, both terminal), `xmin` on both entities,
`CHECK` constraints, a `RESTRICT` foreign key, natural reserve idempotency via
`UNIQUE(order_id, stock_item_id)`, and parallel overselling tests. 138 tests.

Together **276 tests passing**, build clean under `TreatWarningsAsErrors`, database isolation
verified 22/22, and both integration suites proven isolated by running with the compose database
stopped.

Stock's five deliberate divergences from Catalog — no soft delete, no paging, no money, parallel
rather than sequential concurrency tests, rewritten structural guards — are tabulated in
`ARCHITECTURE.md` §5.8.

**Not done:** Ordering. Its database and role are provisioned; there is no project, no code and no
client seam.

---

## Next: build Ordering

Still Phase 0. The point is to create the first real network hops and to prove the conventions
survive a service that orchestrates rather than owns a single resource.

Ordering introduces everything Catalog and Stock lack:

- **A typed `HttpClient` plus an interface seam per downstream service.** The one abstraction
  justified in advance, because it is a real network boundary: the seam the integration tests
  fake, and the seam a broker-based implementation later swaps into.
- **Outbound correlation-id propagation** via a `DelegatingHandler`. Without it a trace breaks
  at the first hop. Neither existing service makes an outbound call, so this is untested
  territory.
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

Two things Stock established that Ordering must honour rather than rediscover:

- **Reserve is naturally idempotent** via `UNIQUE(order_id, stock_item_id)`, so a retried reserve
  returns 409 rather than double-holding. Ordering must combine duplicate product lines in an
  order, or the second will be rejected.
- **Settling is strict, not idempotent.** A retried confirm returns 409 "already confirmed", and
  Ordering must treat that as success. This is decision D3 and it changes in Phase 1/2.

Ordering must also decide how to handle contention: a live 40-way burst against 10 units held only
**4**, because there is no server-side retry on `xmin` conflict (decision D9). Whether Ordering
retries, partial-fills, or fails the order is an open design question, not an implementation
detail.

Expect the conventions to need revision here. **When a convention proves wrong for a later
service, fix it in the earlier ones too** — divergent conventions across three services are worse
than one imperfect convention applied consistently.

This is also where the shared-library trigger fires: the filter, exception handler and correlation
middleware are already copied twice (~517 lines in Stock), and the error handler's
chain-describing logic is entirely generic. Raise the decision **before** the third copy.

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

Also resolves, or forces a decision on, the three open observability items in `KNOWN-ISSUES.md`:
the `Database.Command` Error entry for handled 409s, the fact that validation rejections are
invisible, and the verbose `DbUpdateConcurrencyException` log line that dominates Stock's output
under contention. The last is the cheapest and is a candidate to fix earlier — but it belongs in
**both** handlers, so it is naturally batched with the shared-library decision that Ordering
triggers.

**Contention is the phase's real driver.** Stock measured a 40-way burst holding only 4 of 10
units because there is no server-side retry on `xmin` conflict (decision D9). Retry policies plus
idempotency keys are what turn that 409 into a transparent retry, which is exactly why idempotency
comes first: retrying a reserve without a key double-holds stock.

Note that Phase 2 largely dissolves this problem rather than solving it — a single-consumer queue
serialises reservations, so the contention disappears. If Phase 2 arrives quickly, the retry work
here may be worth less than it looks.

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
