namespace AgenticShop.Catalog.Domain;

/// <summary>
/// A sellable item. Owned exclusively by the Catalog service — Stock and Ordering
/// reference products by <see cref="Id"/> over HTTP and never join against this table.
/// </summary>
public class Product
{
    public const int SkuMaxLength = 64;
    public const int NameMaxLength = 200;
    public const int DescriptionMaxLength = 2000;
    public const int CurrencyLength = 3;
    public const string DefaultCurrency = "USD";

    /// <summary>EF Core materialisation only.</summary>
    private Product()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>
    /// Immutable once created: this is the identity key other services hold,
    /// so changing it would silently orphan their references.
    /// </summary>
    public string Sku { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public decimal Price { get; private set; }
    public string Currency { get; private set; } = DefaultCurrency;
    public bool IsActive { get; private set; } = true;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public static Product Create(
        string sku,
        string name,
        string? description,
        decimal price,
        string? currency = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sku);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegative(price);

        var now = DateTimeOffset.UtcNow;

        return new Product
        {
            Id = Guid.NewGuid(),
            Sku = sku.Trim(),
            Name = name.Trim(),
            Description = Normalize(description),
            Price = NormalizePrice(price),
            Currency = NormalizeCurrency(currency),
            IsActive = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
    }

    public void Update(string name, string? description, decimal price)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegative(price);

        Name = name.Trim();
        Description = Normalize(description);
        Price = NormalizePrice(price);
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Soft delete. History stays queryable for orders that already reference the product.
    /// </summary>
    public void Deactivate()
    {
        IsActive = false;
        UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static decimal NormalizePrice(decimal price)
        => decimal.Round(price, 2, MidpointRounding.AwayFromZero);

    private static string NormalizeCurrency(string? currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
        {
            return DefaultCurrency;
        }

        var normalized = currency.Trim().ToUpperInvariant();

        return IsIso4217Code(normalized)
            ? normalized
            : throw new ArgumentException(
                $"Currency must be a {CurrencyLength}-letter ISO 4217 code such as 'USD' or 'EUR'.",
                nameof(currency));
    }

    /// <summary>
    /// A format check only — three ASCII letters, per ISO 4217. The registry of assigned
    /// codes is deliberately not embedded: it changes as codes are added and withdrawn, and
    /// a stale allow-list would reject legitimate values while giving false confidence.
    /// </summary>
    private static bool IsIso4217Code(string value)
        => value.Length == CurrencyLength && value.All(char.IsAsciiLetter);
}
