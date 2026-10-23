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
| Filter, exception handler and correlation middleware are copied per service | ~~third copy now, extract later~~ **extracted** | `DECISIONS.md` → "Shared infrastructure library" | **done**, first Phase 1 item |
| Order lines snapshot `ProductName` and `UnitPrice` | standing project invariant | `src/AgenticShop.Ordering/docs/DECISIONS.md` → **O5** | never — it is what keeps order history immutable |
| `OrderNumber` is derived, not sequenced | no allocation, no sequence to reset | `src/AgenticShop.Ordering/docs/DECISIONS.md` → **O4** | never, unless a sequence is genuinely needed |

The three-host path was also driven by hand, because no automated test can: `http/ordering.http`
provisions a product in Catalog, stock in Stock, then places an order through Ordering and reads both
databases back. That run is what proved outbound correlation-id propagation end to end — the evidence
is recorded in `src/AgenticShop.Ordering/docs/ARCHITECTURE.md` §6.

---

## Phase 1 — Reliability and observability *(complete)*

Now that a network hop exists, make its failure modes survivable and visible.

- ~~**Extract the shared infrastructure library.**~~ **Done.** `src/AgenticShop.Shared/` holds
  `DataAnnotationValidationFilter`, `CorrelationIdMiddleware`, `IRequestContract` and the
  `ProblemDetailsExceptionHandler` base class; the three services now derive from it rather than
  copy it. Ordering's two divergences were reconciled into the shared surface rather than forked:
  the `orderId` extension became data on `ExceptionClassification`, and the 502 arm stayed in
  Ordering because "a dependency failed" is only expressible where a dependency exists. The
  per-service structural guards and the test harness were **not** shared, and two of those guards
  had to be re-anchored because they had been discovering DTOs through the now-shared marker
  interface. `DECISIONS.md` → "Shared infrastructure library".
- ~~**Idempotency keys** on `POST /orders`.~~ **Done.** A required `Idempotency-Key` header, a claim
  row committed before the first side effect, and a completion written in the same transaction as the
  order. This closes the largest residual the repository had and is what makes the retry work below
  safe — which is why it precedes resilience rather than following it.
  `src/AgenticShop.Ordering/docs/DECISIONS.md` → **O17**.
- ~~Retry, circuit-breaker and timeout policies on the typed clients.~~ **Done.**
  `Microsoft.Extensions.Http.Resilience` on both of Ordering's clients, with retry enabled **per
  request** rather than per client. `src/AgenticShop.Ordering/docs/DECISIONS.md` → **O18**.

  This item expected reserve to need nothing, on the reasoning that Stock's reserve is already
  idempotent through `UNIQUE(order_id, stock_item_id)` (Stock decision D2) and that confirm's
  strictness is handled semantically by Ordering treating "already confirmed" as success (O9, O10).
  The confirm half held. **The reserve half was wrong, and it is worth recording why:** D2 makes the
  *server* idempotent, but it reports the duplicate as a 409, and Ordering's client reads a reserve
  409 as "this line cannot be held". So a retry after a first attempt that committed but whose 201 was
  lost would strand that hold and report the order out of stock. Reserve is therefore exempt from
  retry; the reads and the settles are not, because a 409 from either is resolved by the
  reconciliation read rather than interpreted.

  Note also what this did **not** fix: the contention measurement below. Stock's `xmin`-conflict 409
  is a normal 4xx and is not retried, so retry at the HTTP layer leaves it untouched.
- ~~Serilog structured logging.~~ **Done in all three services.** Catalog went first as the
  reference implementation and the rest followed the same day; the helper now lives in
  `AgenticShop.Shared`. `docs/DECISIONS.md` → "Serilog, adopted in Catalog first". Both
  observability items that waited on it are closed, and the `Database.Command` one turned out to have
  been **diagnosed wrongly**: the plan was a filter that inspected the exception, and EF Core attaches
  no exception to that event, so no filter could ever have worked. The category is silenced by level
  instead, extending the decision already taken for `Microsoft.EntityFrameworkCore.Update`.
- ~~**OpenTelemetry** distributed tracing across the three hosts.~~ **Done**, in all three at once —
  it could not be staged per service the way Serilog was, because the correlation id lives in shared
  middleware and ASP.NET Core populates `Activity.Current` whether or not tracing is registered. The
  warning above was heeded: the correlation id **is** `Activity.Current.TraceId`, so there is one
  concept rather than two. `docs/DECISIONS.md` → "OpenTelemetry, and the correlation id became the
  trace id". Verified end to end with a caller-supplied `traceparent`: one trace id across all three
  services' logs, and Catalog's server span parented to Ordering's client span. The cost is a
  **removed public contract** — inbound `X-Correlation-Id` is no longer adopted, so a hand-run smoke
  request can no longer choose a greppable id and reads it off the response instead.
- ~~Dependency-aware health checks.~~ **Done.** All three services now check their own database —
  `/health` previously registered no checks at all and reported Healthy with PostgreSQL down. Ordering
  additionally maps `/health/ready`, which probes Catalog and Stock; `/health` deliberately does not,
  because a liveness endpoint that depends on another service turns one outage into two. Measured:
  with Catalog stopped, Ordering's `/health` answered 200 in 7 ms while `/health/ready` answered 503
  naming Catalog. `docs/ARCHITECTURE.md` §4.10.
- ~~Dockerfiles for all three services plus a `full` compose profile.~~ **Done.** One shared
  `docker/Dockerfile.service`, parameterised by project name and built against the repository root,
  behind an opt-in profile so the default `docker compose up -d` still starts PostgreSQL alone and
  the host inner loop keeps working. *(Phase 2 added RabbitMQ to the default profile, so it now
  starts PostgreSQL **and** the broker; the reasoning here is about the three APIs and is unchanged.)*
  Verified with all four containers healthy, an end-to-end
  placement driven across them, and one trace id in all three logs.
  `docs/DECISIONS.md` → "One Dockerfile for three services". Bringing the services up on the host's
  own ports also exposed a latent test-isolation defect — the readiness probe froze its target URL at
  registration, so it dialled `localhost:5081` instead of the factory's unresolvable override — now
  fixed and pinned by a guard.
- ~~CI: build, test, and `dotnet ef migrations has-pending-model-changes` as a drift gate.~~
  **Done.** One GitHub Actions workflow, one job, Release configuration: build, all 517 tests, then the
  drift gate per service. `docs/DECISIONS.md` → "CI as a drift gate, not a deployment pipeline". The
  gate earns its step for a measured reason rather than a tidy one: adding an unmapped property to
  `Product` failed **35 of Catalog's 40 integration tests**, but through EF's `PendingModelChangesWarning`
  thrown inside `MigrateAsync()` — which §4.4 confines to Development. On the production path nothing
  runs migrations at startup, so that warning never fires and the same drift surfaces much later as a
  `42703` on the first query selecting the column. The gate is what covers that path.

The extraction came first on purpose: Serilog and OpenTelemetry both touch the logging arms, so
wiring them before consolidating would have meant writing the same change three times.

Also resolves, or forces a decision on, the three observability items in `KNOWN-ISSUES.md`:
the `Database.Command` Error entry for handled 409s, the fact that validation rejections are
invisible, and the verbose `DbUpdateConcurrencyException` log line that dominated Stock's output
under contention. **All three are now resolved** — the third by the shared-handler fix the extraction
made cheap, the first two by the Serilog migration that followed it in all three services.

What the migration did *not* do is add per-field detail to a validation rejection: the request log
says that a request was rejected and where, not which field failed. That is deliberate, and recorded
as an accepted residual rather than left implicit.

**Contention was the phase's stated driver, and the phase did not dissolve it.** Stock measured a
40-way burst holding only 4 of 10 units because there is no server-side retry on `xmin` conflict
(decision D9) — the measurement and the log are in `src/AgenticShop.Stock/docs/ARCHITECTURE.md` §7.
This paragraph originally claimed that retry policies plus idempotency keys were "what turn that 409
into a transparent retry". **That was wrong, and the retry item above already said so:** Stock's
conflict 409 is a normal 4xx and is not retried, so the burst still holds 4 of 10. The item is handed
to Phase 2, where a single-consumer queue serialises reservations and the contention disappears
rather than being retried away.

Idempotency keys were still worth doing first — they closed the largest residual the repository had —
just not for that reason. Note also that Phase 2 largely dissolves the contention problem rather than
solving it, so if Phase 2 arrives quickly the retry work here is worth less than it looks.

### What closed the phase

Every item above is done, and the one open `KNOWN-ISSUES.md` item this phase owned is resolved.

**The "single 5xx observed once under three-container load" was real, reproducible on demand, and
misdiagnosed.** It was not a PostgreSQL deadlock. `SettleAsync` read the reservation and its stock
item in two statements; under READ COMMITTED each takes its own snapshot, so a winner committing
between them left the loser with a stale `Pending` reservation beside a fresh `Reserved = 0`. The
state machine passed on the stale half, the counter guard threw `InvalidOperationException` on the
fresh half, and because that happens *before* `SaveChangesAsync` the `xmin` token never ran — an
ordinary conflict reported to the caller as a 500. Measured at **7 in 10,000 parallel confirms and
6 in 10,000 parallel releases**, with the counters correct in every iteration, so it was a reporting
defect and never a safety one. Fixed with one LEFT JOIN, re-measured at **0 × 500 across 45,000
requests**, and guarded by a statement count rather than a burst, because 7 in 10,000 cannot be raced
reliably. Stock decision **D15**; full account in `docs/KNOWN-ISSUES.md`.

Two of the three claims on file were wrong in ways worth carrying into Phase 2. It needed no
three-container load to reproduce — one container was enough. And "five subsequent full-solution runs
were clean" was never evidence of absence: at that rate a single ten-request test sees it roughly once
in a hundred runs, so five clean runs is the *expected* result.

Verified at closure: `dotnet build` clean under `TreatWarningsAsErrors`; **519 tests passing**
(Shared 45 unit · Catalog 68 unit + 40 integration · Stock 82 unit + 64 integration · Ordering
142 unit + 78 integration); database isolation **22/22**.

---

## Phase 2 — Asynchronous messaging ← the inflection point

- ~~RabbitMQ in compose.~~ **Done (2026-10-23).** `rabbitmq:4-alpine` in the **default** profile
  rather than behind `full`: a broker has no host-process equivalent and no port to collide with, so
  hiding it would leave `dotnet watch` — the loop this phase is developed in — with nothing to
  publish to. AMQP only on `127.0.0.1:5672`, no management plugin, a named volume so durable queues
  survive a recreate, and a `rabbitmq-diagnostics ping` probe because the alpine image ships no
  `curl`. Nothing publishes to it yet; the outbox is the next increment.
  `docs/DECISIONS.md` → "RabbitMQ, in compose's default profile".

  It surfaced two things worth carrying forward. The compose credential guard filtered on the
  literal word `PASSWORD`, which RabbitMQ's canonical `RABBITMQ_DEFAULT_PASS` does not contain — a
  literal broker password would have passed all three services' tests, and the classifier now matches
  an assignment *shape* with a regression test pinning it. And the broker user arrives with the
  `administrator` tag, because the image's env-var bootstrap cannot create a scoped one; accepted for
  a loopback-bound throwaway container and recorded as a divergence to revisit the moment it is
  reachable off-machine.
- ~~Replace synchronous reservation with events: `OrderPlaced` → Stock consumes → `StockCommitted` /
  `StockReservationRejected`.~~ **Done (2026-10-23).** The synchronous reserve and confirm are gone
  from Ordering's placement path, both consumers are registered in their services' `Program.cs`, and
  `POST /orders` answers **202** with a `Pending` order that the outcome consumer settles. Stock takes
  every line of an order or none of them and answers with a single outcome, which is why the
  transition is binary and O14's confirm-phase policy has no equivalent on this path — there is no
  partial outcome to classify. `mandatory` is `true` on both publishers now that queues are bound.
  Verified end to end through the compose stack: 202 → `Pending`, then `Confirmed` on a poll,
  `quantity_on_hand` 10 → 7 with `reserved` back to 0, one inbox row and one drained outbox row on
  each side. `src/AgenticShop.Stock/docs/DECISIONS.md` → **D16**,
  `src/AgenticShop.Ordering/docs/DECISIONS.md` → **O20**.

  **The cleanup this deferred is done (2026-10-23).** Stock's HTTP reservation endpoints and
  Ordering's `IStockClient` are deleted along with the code only they exercised — Ordering's
  `StockReservationSnapshot`, the two exceptions nothing threw (`StockUnavailableException`,
  `OrderPlacementIncompleteException`) and their handler arms, the `DownstreamResilience.NotRetryable`
  marker, the `Services:Stock:BaseUrl` key, and Stock's `ReserveStockRequest` / `ReservationResponse`
  contracts. 40 tests went with them (34 Stock integration, 6 Ordering unit) and one replaced them, so
  the suite is 494 rather than 533. The contention measurement in Stock's `ARCHITECTURE.md` §7 is
  marked historical.

  The one replacement is worth naming, because §6 requires a parallel test for anything with a
  mutable counter and the eight deleted ones no longer had a subject: they raced the HTTP reserve
  endpoint against itself, and the consumer that replaced it holds serially (`prefetchCount: 1`,
  `consumerDispatchConcurrency: 1`), so it cannot race itself. `ConsumerStockContentionTests` races
  the consumer against `PUT /api/v1/stock/{productId}` instead — the only cross-writer contention
  left on `stock_items`. It asserts inequalities rather than counts, because a message that loses
  the `xmin` race twice is dropped without an inbox row, so how many orders commit is the
  scheduler's business: no 5xx, `reserved` never leaks, the counter agrees with the reservation
  rows, and nothing is oversold.
- ~~**Outbox pattern.**~~ **Done on the producer side (2026-10-23).** An `outbox_messages` table, an
  `OutboxPublisher` hosted service and an `OrderPlaced` contract — carrying the stable message id
  this bullet called cheap to decide now and expensive to retrofit. The row joins the order's own
  `SaveChangesAsync`, so the two commit together or not at all. Nothing consumes the message yet and
  reservation is still synchronous; 2b adds the consumer and deletes the sync path in one commit.
  `src/AgenticShop.Ordering/docs/DECISIONS.md` → **O19**.
  **Correction to what this bullet used to claim:** the design was *nearly* outbox-ready, but
  placement is not a single `SaveChangesAsync`. Ordering runs two — the idempotency claim in
  `ClaimAsync`, before any reservation happens, and the order + lines + key completion in
  `WriteAsync`. An outbox row is atomic with the order only in the second, which is where it went;
  the claim transaction holds no order row to be atomic with. Both saves are cited by method name
  rather than line number now, because implementing this bullet moved them
- **Inbox** for consumer-side deduplication. Prerequisite: the Phase 2 event contracts must carry
  a stable message id from the start — cheap to decide then, expensive to retrofit
- Saga / process manager in Ordering owning the order lifecycle and compensation — including
  driving Phase 0's terminal `PartiallyConfirmed` orders to a resolved state, which a synchronous
  orchestrator with no worker can only record. It also makes a persisted `Pending` order the normal
  case. **Correction:** that needs no status migration. `Pending` is already an `OrderStatus`
  member, already listed in `ck_orders_status_known`, already emitted verbatim by the initial
  migration, and already what `Order.Create` sets — Phase 0 wrote the vocabulary and simply never
  persisted it, and `ContractSchemaAlignmentTests` keeps enum and CHECK in step. What a saga does
  need is the outbox table, plus `UpdatedAtUtc` and a live `xmin` on `Order` if it updates rows
  rather than writing each one once
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
