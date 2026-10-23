# Known issues — Stock Service

Issues specific to Stock. Repository-wide and shared-infrastructure items live in
`../../../docs/KNOWN-ISSUES.md`; Catalog's in
`../../AgenticShop.Catalog/docs/KNOWN-ISSUES.md`.

Severity: **Medium** a correctness or observability gap · **Low** hygiene · **Info** a trade-off
worth knowing.

---

## Open

### Concurrency

**The consumer's single-statement read has no structural guard.** *Low.*
`OrderPlacedConsumer.ReserveAsync` reads every stock item an order names in **one statement**, and
that is load-bearing rather than tidy: two statements would take two READ COMMITTED snapshots, and a
decision computed from rows of mixed vintage is the bug recorded as **D15** and in `ARCHITECTURE.md`
§7. The test that counted the statements a settle actually issued was deleted along with the endpoint
it measured, so the rule is now enforced by a code comment alone — and the repository-root
`AGENTS.md` §6 is explicit that a convention enforced only by prose will be violated. Restoring a
guard means capturing SQL from inside the consumer's scope, which is what the deleted
`SettleSqlCapture` did for the HTTP path.

**A message that loses the `xmin` race twice is dropped.** *Medium · Phase 3.*
The consumer requeues once and then acknowledges without writing an inbox row, because with no
dead-letter queue a nack either discards the message anyway or loops forever filling the log. The
consequence is that `OrderPlacedConsumerTests` cannot assert every published message was processed,
and an order can stay `Pending` in Ordering with no outcome ever arriving — the stranded-`Pending`
residual in `../../../docs/KNOWN-ISSUES.md`. Only contention with `PUT /api/v1/stock/{productId}`
can provoke it now, so it is rare; a DLQ plus a reservation-expiry worker is what closes it.

### API

**Stock cannot validate that a product exists.** *Info · deliberate.* As a leaf service it makes
no outbound call, so stock can be provisioned for a `ProductId` that Catalog has never heard of.
Accepted because the only caller in the real flow is Ordering, which validates first. If a direct
admin path to Stock is ever exposed, this becomes a real gap.

### Deliberate, not defects

**Reservation transitions are strict, not idempotent.** A second `Confirm()` on one row throws rather
than succeeding quietly. Deliberate (decision D3): a silent no-op would hide a double-settle. Nothing
outside this process can provoke it now that the confirm endpoint is gone — the consumer confirms
each line once, inside the transaction that reserves it — so the strictness is what makes a redelivered
message loud instead of quietly double-shipping. Redelivery safety itself is the inbox's job, not
this.

**`Confirmed` is non-compensable, and every hold now reaches it.** Deliberate, and sharper since the
consumer began reserving and confirming in one transaction: there is no `Pending` hold to release, so
if cancellation ever arrives there is nothing to undo. `StockItem.ReleaseReservation` and the
`Released` status are kept for that conversation rather than deleted as unreachable — see
`Messaging/OrderPlacedConsumer.cs` remarks and decision **D16**.

**One bounded retry on `xmin` conflict, in the consumer.** Decision D9's "no server-side retry" was
about the HTTP reserve endpoint, which is gone. The consumer does retry: it nacks and requeues once,
then drops. Bounded because a requeue loop with no dead-letter queue is a spin, and one attempt is
enough for a conflict with `PUT /api/v1/stock/{productId}`, which is a single short transaction.

**No list or paging endpoint.** Deliberate; nothing needed it and symmetry with Catalog is not a
reason. The one list endpoint Stock had — `GET /api/v1/reservations?orderId=` — was deleted with the
rest of the reservation surface, so inspecting holds is a `psql` query now.

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
