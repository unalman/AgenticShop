# AGENTS.md — Catalog Service

Read the repository-root `../../AGENTS.md` first. It holds the global rules: service and
database boundaries, technology, shared coding conventions, testing rules, configuration,
git rules and the list of things not to introduce. **This file only adds what is specific to
Catalog.**

Design detail: `docs/ARCHITECTURE.md` · decisions: `docs/DECISIONS.md` ·
open and resolved issues: `docs/KNOWN-ISSUES.md`.

## Identity

| | |
|---|---|
| Port | **5081** — fixed in `Properties/launchSettings.json` |
| Database | `agenticshop_catalog` |
| Role | `catalog_svc` (least privilege; never the `agenticshop` superuser) |
| Entity | `Product` |
| Connection string key | `ConnectionStrings:Catalog` |

## Rules specific to this service

- **Soft delete is mandatory here and must not be copied elsewhere.** `DELETE` calls
  `Deactivate()` and returns `204`; it never removes a row. `HasQueryFilter(p => p.IsActive)`
  hides deactivated products, and `includeInactive=true` opts out. A deactivated product 404s on
  `GET`, `PUT` and `DELETE`. There is deliberately no reactivation path.
- **`Sku` is immutable.** `UpdateProductRequest` does not carry it. `Sku` is the identity key
  other services hold; changing it would silently orphan their references.
- **`Currency` is immutable too**, and is validated as exactly three ASCII letters per ISO 4217 —
  not merely three characters. Normalise with `ToUpperInvariant`, never `ToUpper`: this host's
  culture is Turkish, where `ToUpper` turns `i` into a non-ASCII dotted capital.
- **Money is `decimal` + a separate `Currency` string**, rounded 2dp `AwayFromZero` to match
  `numeric(18,2)`. No `Money` value object.
- **List endpoints order by a unique column** (`Sku`). Paging over `Name` is unstable and can
  skip or repeat rows. Clamp `page` to `>= 1` and `size` to `1..MaxPageSize`.
- **Do not add an index on `is_active`.** The query filter selects most rows, so a sequential scan
  is optimal and the index would be ignored.
- **Do not add `HasDefaultValue` to `IsActive`.** EF's `bool` sentinel is `false`, so an explicit
  `false` would be dropped from the INSERT and come back as the default.
- Length limits live as `public const int` on `Product` and are referenced by both `Contracts/`
  and `Data/`. `ContractSchemaAlignmentTests` fails if a DTO admits more than its column.

## Commands

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet ef migrations add <Name> -p src/AgenticShop.Catalog -s src/AgenticShop.Catalog
dotnet ef database update       -p src/AgenticShop.Catalog -s src/AgenticShop.Catalog
dotnet run --project src/AgenticShop.Catalog
dotnet test tests/AgenticShop.Catalog.UnitTests          # no Docker needed
dotnet test tests/AgenticShop.Catalog.IntegrationTests   # needs Docker
```

Manual requests: `../../http/catalog.http`. Scalar UI: <http://localhost:5081/scalar/v1>
(Development only).

## Before changing a convention here

Catalog is the reference implementation, so a change to a *convention* it originated — entity shape,
EF configuration, endpoint style, the four error invariants — must be mirrored into Stock **and
Ordering**. The cross-cutting code itself is no longer Catalog's to change: the validation filter,
the correlation middleware, `IRequestContract` and the exception-handler base class live in
`src/AgenticShop.Shared/`. Which conventions are shared: `../../docs/ARCHITECTURE.md` §4.
