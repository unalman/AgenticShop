# Roadmap

Each phase adds one capability, and only once the previous phase has created the problem that
justifies it. The ordering is the point: **retrying without idempotency keys double-reserves
stock; an outbox without a broker to publish to is dead weight; CQRS without read pressure is
ceremony.**

`README.md` carries a short version of this list. This file is authoritative for the phases.
Constraints on what may *not* be added early are in `AGENTS.md` §8; the reasoning behind each
deferral is in `DECISIONS.md`. Decisions taken while building a service live in that service's
`docs/DECISIONS.md`, not here — this file records only which phase owns each open problem.

---

## Phase 0 — Foundation *(complete)*

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

**Ordering — done.** The orchestrator, and the first service to make an outbound call: two typed
`HttpClient`s behind consumer-owned interfaces, outbound correlation-id propagation, the first real
nested request DTO, order-line snapshotting, and compensation across services. One migration, two
endpoints plus `/health`, four order statuses, `xmin` on both entities, `CHECK` constraints, a
`CASCADE` foreign key and a unique index enforcing one line per product. 150 tests.

Together **426 tests passing**, build clean under `TreatWarningsAsErrors`, database isolation
verified 22/22, and all three integration suites proven isolated by running with the compose database
stopped.

Each service's deliberate divergences from Catalog are tabulated in its own
`docs/ARCHITECTURE.md` — Stock §8, Ordering §10. Those tables are the record of what transferred
and what did not; do not restate them here.

### What Phase 0 settled, and who owns it now

| Settled | Decision | Owner of the detail | Next phase to touch it |
|---|---|---|---|
| A reserve that fails after earlier lines were held → release everything, fail the order, **no server-side retry** | fail fast | `src/AgenticShop.Ordering/docs/DECISIONS.md` → **O16** | Phase 1, once idempotency keys make a retry safe |
| A confirm that fails part-way → terminal `PartiallyConfirmed`, 502, classified by a reconciliation read | reconcile, never undo a confirm | `src/AgenticShop.Ordering/docs/DECISIONS.md` → **O14**, design in that service's `ARCHITECTURE.md` §5 | Phase 2's saga, which can drive it to a resolved state |
| Filter, exception handler and correlation middleware are copied per service | third copy now, extract later | `DECISIONS.md` → "No shared library" | **Phase 1**, committed |
| Order lines snapshot `ProductName` and `UnitPrice` | standing project invariant | `src/AgenticShop.Ordering/docs/DECISIONS.md` → **O5** | never — it is what keeps order history immutable |
| `OrderNumber` is derived, not sequenced | no allocation, no sequence to reset | `src/AgenticShop.Ordering/docs/DECISIONS.md` → **O4** | never, unless a sequence is genuinely needed |

The three-host path was also driven by hand, because no automated test can: `http/ordering.http`
provisions a product in Catalog, stock in Stock, then places an order through Ordering and reads both
databases back. That run is what proved outbound correlation-id propagation end to end — the evidence
is recorded in `src/AgenticShop.Ordering/docs/ARCHITECTURE.md` §6.

---

## Phase 1 — Reliability and observability *(next)*

Now that a network hop exists, make its failure modes survivable and visible.

- Retry, circuit-breaker and timeout policies on the typed clients
- **Idempotency keys** on `POST /orders` and on reserve/confirm — a prerequisite for safe retries,
  which is why it precedes resilience rather than following it
- Serilog structured logging
- **OpenTelemetry** distributed tracing across the three hosts — the moment "distributed" stops
  being theoretical. Derive the correlation id from `Activity.Current?.TraceId` here, or there
  will be two parallel correlation concepts
- Dependency-aware health checks
- **Extract the shared infrastructure library.** Committed by the 2026-09-24 decision that gave
  Ordering the third copy: `DataAnnotationValidationFilter`, the exception-handler skeleton and
  `CorrelationIdMiddleware`, designed against four consumers (the three plus Serilog/telemetry
  wiring) rather than three near-identical ones. The per-service structural guards stay per
  service. See `DECISIONS.md` → "No shared library". Reconcile Ordering's two divergences — the
  502 bucket and the `orderId` extension — into the shared surface at that point.
- Dockerfiles for all three services plus a `full` compose profile
- CI: build, test, and `dotnet ef migrations has-pending-model-changes` as a drift gate

Also resolves, or forces a decision on, the three open observability items in `KNOWN-ISSUES.md`:
the `Database.Command` Error entry for handled 409s, the fact that validation rejections are
invisible, and the verbose `DbUpdateConcurrencyException` log line that dominates Stock's output
under contention. The last is the cheapest, but it belongs in **all three** handlers, so it is
naturally batched with the extraction above.

**Contention is the phase's real driver.** Stock measured a 40-way burst holding only 4 of 10 units
because there is no server-side retry on `xmin` conflict (decision D9) — the measurement and the log
are in `src/AgenticShop.Stock/docs/ARCHITECTURE.md` §7. Retry policies plus idempotency keys are what
turn that 409 into a transparent retry, which is exactly why idempotency comes first.

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
- Saga / process manager in Ordering owning the order lifecycle and compensation — including
  driving Phase 0's terminal `PartiallyConfirmed` orders to a resolved state, which a synchronous
  orchestrator with no worker can only record. It also makes a persisted `Pending` order the normal
  case, which needs the migration Phase 0 deliberately did not write
- Dead-letter queues, retry policies, at-least-once semantics
- Closes the dual-write orphan residual: a process death mid-confirm currently leaves Stock holding
  reservations for an order row that was never written — `KNOWN-ISSUES.md` → "Cross-service
  consistency"

---

## Phase 3 — Data and scale

- Redis: catalog read caching plus a distributed idempotency store
- Reservation-expiry background worker (the `ExpiresAtUtc` concept deliberately skipped in Phase 0).
  This is what finally lifts a hold stranded by a failed compensation release
- Read models / projections — CQRS on the read side only, introduced when a real query needs it
- Consumer-driven contract tests (Pact), so services can evolve independently — the answer to
  Ordering's consumer-owned contracts drifting silently
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
