# Known issues — Catalog Service

Issues specific to Catalog. Repository-wide and shared-infrastructure items live in
`../../../docs/KNOWN-ISSUES.md`; Stock's in `../../AgenticShop.Stock/docs/KNOWN-ISSUES.md`.

Severity: **Medium** a correctness or observability gap · **Low** hygiene · **Info** a trade-off
worth knowing.

---

## Open

### Testing

**No HTTP-level optimistic-concurrency test.** *Low · deliberate.* The `xmin` token is proven at
the `DbContext` level (`ConcurrencyTests`, two independent units of work) and the 409 mapping is
proven in `CatalogExceptionHandlerTests`. It cannot be provoked deterministically over HTTP: each
request loads the row fresh, so it always carries the current `xmin`. Only two genuinely
overlapping requests collide, and a racing test would be flaky.

Stock is different and **does** have HTTP-level parallel tests, because there the counter is
contended and the consequence of a missed token is overselling rather than a lost price edit. Do
not copy Catalog's lighter approach into a service with a contended counter.

**`CatalogApiFixture.DisposeAsync` is not exception-safe.** *Low.* If `_factory.DisposeAsync()`
throws, `_postgres.DisposeAsync()` never runs and the container leaks until Ryuk reaps it. Wants a
`try/finally`. `StockApiFixture` already has one — the divergence is tracked in
`../../../docs/KNOWN-ISSUES.md` because it spans both services.

**Minor test-debt items.** *Info.* `Deactivate()` bumps `UpdatedAtUtc` but nothing asserts it.
`Create_RejectsNegativePrice` casts `double`→`decimal` while the rounding theory two tests above
deliberately uses invariant-culture strings — the inconsistency invites an imprecise case later.

### Data and API

**Paging is offset-based and not atomic.** *Low.* `CountAsync` and `ToListAsync` are two queries
outside a transaction, so `TotalCount` can disagree with the page under concurrent writes.
Standard, and acceptable at this scale. Keyset paging is the eventual answer.

**Soft-deleted products cannot be reactivated.** *Low.* `Update` does not accept `IsActive` and no
endpoint exposes it, so an accidental `DELETE` is only reversible with SQL. Possibly intentional;
never explicitly decided.

**`Product.Create` assigns its own `Guid`.** *Info.* A caller cannot supply a deterministic id.
Fine for idempotency keyed on a client token, but worth knowing before designing client-generated
identities. Stock's `StockItem.Create` does the same.

### Deliberate, not defects

**`Currency` carries both `[StringLength(3,3)]` and `[RegularExpression]`.** Redundant on length,
intentional: one guards the column, the other the charset, and `ContractSchemaAlignmentTests`
depends on `StringLength` being present.

---

## Resolved

Kept for the lesson, not the fix. Findings about shared infrastructure — the validation filter,
the exception handler, the test harness, `xmin`, `launchSettings` — are recorded in
`../../../docs/KNOWN-ISSUES.md`, because that is where the code now lives in both services.

### `HasDefaultValue(true)` on a non-nullable `bool`

EF's sentinel for `bool` is `false`, so an explicit `false` would be dropped from the INSERT and
the database default applied instead — a product created inactive would come back active.
Latent: unreachable while `Create` always set `true`, which is exactly why it was dangerous.
Fixed by removing the default in migration `AddXminConcurrencyAndDropIsActiveDefault`.
**Caught by:** reading the model snapshot.

This is the only resolved finding that is genuinely Catalog-specific. Everything else from the
Catalog-era reviews concerned code that has since been copied into Stock, so it is documented
once, globally.
