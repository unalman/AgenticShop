# Known issues — Stock Service

Issues specific to Stock. Repository-wide and shared-infrastructure items live in
`../../../docs/KNOWN-ISSUES.md`; Catalog's in
`../../AgenticShop.Catalog/docs/KNOWN-ISSUES.md`.

Severity: **Medium** a correctness or observability gap · **Low** hygiene · **Info** a trade-off
worth knowing.

---

## Open

### Concurrency

**Contention throughput is poor by design.** *Medium · Phase 1 (retry) or Phase 2 (serialisation).*
A live 40-way burst against 10 units held only **4** — 36 requests lost the `xmin` race and were
told 409 despite stock being available. Nothing is oversold and nothing is a 5xx, so this is
correct optimistic-concurrency behaviour with no server-side retry (decision D9), not a defect.

It matters because Ordering's synchronous order placement will reserve line by line and will fail
often under load. Either Phase 1 adds retry, or Phase 2's single-consumer queue dissolves the
contention entirely. Ordering must decide in the meantime whether to retry, partial-fill, or fail
the order.

A 20-way race to confirm one reservation behaved correctly: exactly one 200, nineteen 409s, no
5xx, and the goods shipped once.

### API

**Stock cannot validate that a product exists.** *Info · deliberate.* As a leaf service it makes
no outbound call, so stock can be provisioned for a `ProductId` that Catalog has never heard of.
Accepted because the only caller in the real flow is Ordering, which validates first. If a direct
admin path to Stock is ever exposed, this becomes a real gap.

### Deliberate, not defects

**Reservation transitions are strict, not idempotent.** A retried confirm returns 409 rather than
succeeding quietly. Deliberate (decision D3): a silent no-op would hide a double-settle from the
caller. **Ordering must treat that specific 409 as success** until Phase 1/2 adds real
idempotency.

**No server-side retry on `xmin` conflict.** Deliberate (decision D9); see the contention item
above.

**No list or paging endpoint.** Deliberate; nothing needed it and symmetry with Catalog is not a
reason. `GET /api/v1/reservations?orderId=` exists because it is the reconciliation and
compensation path, not because listing is generally useful.

**`UNIQUE(order_id, stock_item_id)` forces one hold per product per order.** Deliberate (decision
D2) — it is what makes reserve naturally idempotent. The consequence is that **Ordering must
combine duplicate product lines**, or the second will be rejected as a duplicate.

---

## Resolved

Kept for the lesson, not the fix. Findings about shared infrastructure are recorded in
`../../../docs/KNOWN-ISSUES.md`.

### `GetCheckConstraints()` throws against EF's runtime model

Writing Stock's schema-alignment guard failed with "The requested configuration is not stored in
the read-optimized model". Check constraints are a design-time concept; reading them back needs
`db.GetService<IDesignTimeModel>().Model`, and that type is in
`Microsoft.EntityFrameworkCore.Metadata`, not `.Infrastructure`. **Caught by:** the compiler, then
a test failure.

Relevant to any future service that adds `CHECK` constraints and wants to assert on them.

### A copied structural guard would have passed vacuously

Catalog's `ContractSchemaAlignmentTests` compares DTO string limits against column limits. Stock has
almost no length-constrained strings, so a verbatim copy would have matched zero properties and
passed while proving nothing — the exact failure its non-vacuity assertion exists to catch. Fixed by
rewriting the guard around Stock's real drift risks: the enum vocabulary versus its `CHECK` list,
status column width, quantity bounds versus `StockItem.MaxQuantity`, derived values not persisted,
and an `xmin` token on every entity.

**Caught by:** reasoning about the copy before making it, which is why the repository-root
`AGENTS.md` §10 says to verify a convention still fits rather than duplicating it blindly.

### A test asserted a 404 the API was right to return

`ListByOrder_ReturnsEveryReservationWithItsProduct` seeded a third reservation for a product with
no stock record. Stock correctly answered 404 and the assertion failed. The test was wrong, not the
code. **Caught by:** the test itself.

### FluentAssertions API misuse in new test code

`ThrowAsync<T>().Subject` does not yield the exception — it is `.And`. `NotContain(char)` has no
overload, so a `string` is required. `BeExactly` does not exist on the relevant assertion type.
Three separate compile breaks in one pass. **Caught by:** the compiler.
