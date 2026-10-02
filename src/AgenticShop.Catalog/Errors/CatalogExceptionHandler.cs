using AgenticShop.Catalog.Data;
using AgenticShop.Shared.Errors;

namespace AgenticShop.Catalog.Errors;

/// <summary>
/// Catalog's half of the error contract. It has no domain exceptions of its own — every arm it
/// needs is one the shared skeleton already provides — so this supplies only the two messages that
/// name a Catalog resource.
/// </summary>
/// <remarks>
/// The four invariants, the classification order, the logging severity rules and the PostgreSQL
/// chain description all live in <see cref="ProblemDetailsExceptionHandler"/>. Behaviour is
/// specified once there; <c>CatalogExceptionHandlerTests</c> covers only what is Catalog-specific.
/// </remarks>
public sealed class CatalogExceptionHandler(ILogger<CatalogExceptionHandler> logger)
    : ProblemDetailsExceptionHandler(logger)
{
    protected override string ConcurrencyDetail =>
        "The product was changed by another request. Reload it and try again.";

    /// <summary>
    /// Keyed on the constraint name so the message stays true when a second unique index is added.
    /// A hardcoded "duplicate SKU" would otherwise be reported for any unique violation on the
    /// table.
    /// </summary>
    protected override string UniqueViolationDetail(string? constraintName) => constraintName switch
    {
        ProductConfiguration.UniqueSkuIndexName => "A product with that SKU already exists.",
        _ => FallbackConflictDetail
    };
}
