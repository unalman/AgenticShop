# Decisions — Catalog Service

Why Catalog is built the way it is. Global decisions — platform, topology, boundaries, the shared
error contract, validation, testing strategy, security — live in
`../../../docs/DECISIONS.md` and are not repeated here.

Catalog was built first, so several conventions that are now global originated here. What follows
is only what is **specific to Catalog** and must not be assumed by another service.

---

## Soft delete

`IsActive` + `Deactivate()` + `HasQueryFilter`, with `includeInactive=true` as the escape hatch.

**Why.** History stays queryable for orders that already reference a product. A hard delete would
leave `OrderLine.ProductId` pointing at nothing once Ordering exists.

**Consequences accepted.** Deactivated entities 404 on every route — `GET`, `PUT` and `DELETE` —
and there is **no reactivation path**, so an accidental delete is only reversible with SQL.
Possibly intentional; never explicitly decided.

**This is the single most important convention not to copy.** Stock has no soft delete: a silently
filtered stock row reads as "no stock", which is more dangerous than a deleted product. An order
must never vanish from a list either.

---

## Offset paging ordered by a unique column

`OrderBy(Sku)`, not `Name`. Paging over a non-unique column is unstable — rows can be skipped or
repeated across pages.

`CountAsync` plus `ToListAsync` are two queries outside a transaction, so `TotalCount` can
disagree with the page under concurrent writes; standard for offset paging and accepted.

**Revisit:** keyset paging when a table is large enough to care. Stock has no list endpoint at
all, so paging is not yet a proven cross-service convention.

---

## `decimal` plus a `Currency` column, no `Money` value object

A `Money` type is a DDD tactical construct, and DDD is deferred. `numeric(18,2)` with
`HasPrecision(18, 2)` and a `varchar(3)` currency is sufficient and keeps the schema legible.

Stock has no money at all — its quantities are `int`. Do not add money fields to a service that
does not need them.

---

## Currency validated by format, not by registry

Three ASCII letters per ISO 4217. Embedding the list of assigned codes was rejected: it changes as
codes are added and withdrawn, and a stale allow-list rejects legitimate values while giving false
confidence.

`ToUpperInvariant` rather than `ToUpper` because this host's culture is Turkish, where `ToUpper`
maps `i` to a dotted capital that is not ASCII — `"ils"` would fail validation. A unit test pins
this.

The rule is expressed twice on the wire: `[StringLength(3,3)]` guards the column and
`[RegularExpression("^[A-Za-z]{3}$")]` guards the charset. Redundant on length, intentional —
`ContractSchemaAlignmentTests` depends on `StringLength` being present.

---

## No `HasDefaultValue` on a non-nullable `bool`

**Context.** `IsActive` originally had `HasDefaultValue(true)`.

**Decision.** Removed in migration `AddXminConcurrencyAndDropIsActiveDefault`. The domain always
assigns `IsActive`.

**Why.** EF's sentinel for `bool` is `false`. With a store default present, an explicit `false` is
indistinguishable from "unset": EF omits the column from the INSERT and the default is applied, so
a product created inactive would silently come back active. Latent — unreachable while `Create`
always sets `true` — which is exactly why it was dangerous. If a database default is ever needed
for raw SQL inserts, add `.HasSentinel(null)` instead.

**Applies to every service**, not just Catalog, but it was found here and Catalog is the only
service with a non-nullable `bool` today.

---

## No index on `is_active`

The query filter selects the large majority of rows, so a sequential scan is optimal and an index
would be ignored. Adding one would be pure overhead. Worth stating because "add an index for the
filter" is the obvious-looking wrong answer.

---

## `Sku` is immutable after creation

`Update` does not accept it. `Sku` is the identity key other services hold; changing it would
silently orphan their references. Stock's `ProductId` is immutable for the same reason.
