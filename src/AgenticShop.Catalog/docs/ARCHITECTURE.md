# Architecture — Catalog Service

Service-specific design detail. Global architecture — topology, boundary enforcement, the build
system, the shared service conventions and the testing architecture — lives in
`../../../docs/ARCHITECTURE.md`. Read that first; this file covers only what is specific to
Catalog.

Catalog is the **reference implementation**: it was built first so its conventions could be
reviewed before being replicated. Several conventions documented in the global file originated
here and are still exemplified by this service's code.

| | |
|---|---|
| Port | 5081 |
| Database | `agenticshop_catalog` |
| Role | `catalog_svc` |
| Entity | `Product` |
| Migrations | `20260923143431_InitialCatalog`, `20260923154230_AddXminConcurrencyAndDropIsActiveDefault` |

---

## 1. Domain

`../Domain/Product.cs` — a sellable item. Stock and Ordering reference products by `Id` over HTTP
and never join against this table.

| Member | Notes |
|---|---|
| `Sku` | **unique, immutable after creation** — the identity key other services hold; changing it would silently orphan their references |
| `Name`, `Description?` | `Description` normalises blank to `null`, never `""` |
| `Price` | `decimal`, rounded 2dp `MidpointRounding.AwayFromZero` to match `numeric(18,2)` |
| `Currency` | `string`, default `"USD"` |
| `IsActive` | soft-delete flag |
| `CreatedAtUtc`, `UpdatedAtUtc` | `DateTimeOffset` |

Length limits are `public const int` on the entity — `SkuMaxLength = 64`,
`NameMaxLength = 200`, `DescriptionMaxLength = 2000`, `CurrencyLength = 3`,
`DefaultCurrency = "USD"` — so `Contracts/` and `Data/` reference one number instead of three
copies. `ContractSchemaAlignmentTests` asserts the contracts never exceed them.

Methods: `Create`, `Update`, `Deactivate`. `Update` deliberately does **not** accept `Sku` or
`Currency`, and does not accept `IsActive` — see §4.

### Money and currency

Money is `decimal` plus a separate `Currency` string. **No `Money` value object** — that would be
a DDD tactical construct the project has deferred.

Currency is normalised with **`ToUpperInvariant`**, not `ToUpper`. This machine's culture is
Turkish, where `ToUpper` maps `i` to a dotted capital İ that is not an ASCII letter — so `"ils"`
would fail validation under `ToUpper` and pass under `ToUpperInvariant`. A unit test pins this.

Currency is validated as exactly **three ASCII letters** per ISO 4217. The registry of assigned
codes is deliberately not embedded: it changes as codes are added and withdrawn, and a stale
allow-list would reject legitimate values while giving false confidence. On the wire the rule is
expressed twice — `[StringLength(3,3)]` guards the column and `[RegularExpression("^[A-Za-z]{3}$")]`
guards the charset — because `ContractSchemaAlignmentTests` depends on `StringLength` being
present.

---

## 2. Schema

```
products
  id              uuid           PK
  sku             varchar(64)    NOT NULL, UNIQUE  (ix_products_sku)
  name            varchar(200)   NOT NULL
  description     varchar(2000)
  price           numeric(18,2)  NOT NULL
  currency        varchar(3)     NOT NULL
  is_active       boolean        NOT NULL
  created_at_utc  timestamptz    NOT NULL
  updated_at_utc  timestamptz    NOT NULL
  xmin            (system column — emits no DDL)
```

`is_active` has **no database default**, deliberately; see `DECISIONS.md`.

There is deliberately **no index on `is_active`**. The query filter selects the large majority of
rows, so a sequential scan is optimal and an index would be ignored — adding one would be pure
overhead.

The unique index name is exposed as `ProductConfiguration.UniqueSkuIndexName` because
`CatalogExceptionHandler` branches on it to produce the 409 message.

---

## 3. Endpoints

```
GET    /api/v1/products?page=&size=&includeInactive=
GET    /api/v1/products/{id:guid}
POST   /api/v1/products
PUT    /api/v1/products/{id:guid}
DELETE /api/v1/products/{id:guid}
GET    /health
```

`POST` and `PUT` map to `CreateProductRequest` / `UpdateProductRequest`; both implement
`IRequestContract`. `UpdateProductRequest` intentionally omits `Sku` and `Currency`, so the API
cannot express a change to them.

`DELETE` is a soft delete: it calls `Deactivate()` and returns `204`, never removing a row.

Catalog has **no outbound calls** and therefore no `Clients/` folder — but unlike Stock this is
incidental rather than a designed property, since Catalog is the service others call.

---

## 4. Soft delete

`IsActive` on the entity, `Deactivate()` as the only way to clear it, and
`HasQueryFilter(p => p.IsActive)` making deactivated rows invisible by default.

Rationale: history stays queryable for orders that already reference the product. A hard delete
would leave `OrderLine.ProductId` pointing at nothing.

`includeInactive=true` applies `IgnoreQueryFilters()` for the admin view.

Consequences, both accepted:

- `GET`, `PUT` and `DELETE` on a deactivated product all return `404`.
- There is **no reactivation path**. `Update` does not accept `IsActive` and no endpoint exposes
  it, so an accidental `DELETE` is only reversible with SQL.

**This convention is Catalog-specific and must not be copied.** A silently filtered stock row
would read as "no stock", which is more dangerous than a deleted product, and an order must never
vanish from a list. Stock has no soft delete at all.

---

## 5. Paging

`page` is 1-based and clamped to `>= 1`; `size` to `1..MaxPageSize`. Catalog uses
`DefaultPageSize = 20`, `MaxPageSize = 100`. The response is a `ProductPage` record with `Items`,
`Page`, `Size`, `TotalCount`, echoing the clamped values so a caller can see what was applied.

**Order by a unique column** — Catalog uses `Sku`. Ordering by a non-unique column such as `Name`
makes the paging window unstable: rows can be skipped or repeated across pages.

`CountAsync` and `ToListAsync` are two queries outside a transaction, so `TotalCount` can
disagree with the page under concurrent writes. Standard for offset paging and accepted; see
`KNOWN-ISSUES.md`.

Stock has no paging and no list endpoint, and neither does Ordering — paging is still a
Catalog-only convention, not house style.

---

## 6. Error classification specific to Catalog

`CatalogExceptionHandler` follows the shared skeleton in `../../../docs/ARCHITECTURE.md` §4.7.
Its only service-specific part is `UniqueViolationDetail`:

| Constraint | 409 detail |
|---|---|
| `ix_products_sku` | "A product with that SKU already exists." |
| anything else | "The value conflicts with an existing record." |

Catalog has no domain conflict exceptions — `Product` violations are all `ArgumentException`
(400) or DataAnnotations (400 via the filter). The 409 paths are the duplicate SKU and an `xmin`
concurrency conflict on `PUT`.

---

## 7. What Catalog does not exercise

Catalog is single-entity CRUD. It does **not** cover: outbound HTTP calls, typed `HttpClient`
seams, correlation-id propagation, nested request DTOs, multi-entity transactions, compensation,
sequences, or any contended numeric resource. Ordering has since exercised all of those except a
sequence — `../../AgenticShop.Ordering/docs/ARCHITECTURE.md` §11 is the canonical list of what
remains untested anywhere in Phase 0.

Its `xmin` token is proven at the `DbContext` level only (`ConcurrencyTests`, two independent
units of work) — there is no HTTP-level parallel test, because `Product` has nothing worth
racing for. Stock is where concurrency is genuinely tested.
