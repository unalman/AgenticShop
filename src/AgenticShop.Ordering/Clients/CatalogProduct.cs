namespace AgenticShop.Ordering.Clients;

/// <summary>
/// Ordering's own view of a Catalog product: the fields it needs to snapshot an order line and
/// nothing more. Declared here rather than shared, because there is deliberately no shared contract
/// assembly — a consumer owns the contract it depends on.
/// </summary>
/// <remarks>
/// Extra fields in Catalog's response are ignored on deserialisation. That is the right default for
/// a consumer-owned contract: Catalog may add a field without breaking Ordering. The mirror-image
/// risk — Catalog renaming one — is Phase 3's consumer-driven contract tests.
/// </remarks>
public sealed record CatalogProduct(
    Guid Id,
    string Sku,
    string Name,
    decimal Price,
    string Currency,
    bool IsActive);
