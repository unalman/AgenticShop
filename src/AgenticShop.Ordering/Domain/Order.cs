using System.Globalization;

namespace AgenticShop.Ordering.Domain;

/// <summary>
/// A customer order. Owned exclusively by Ordering; <see cref="OrderLine.ProductId"/> is a plain
/// <see cref="Guid"/> because the product lives in Catalog's database, and the reservations that
/// cover it live in Stock's.
/// </summary>
/// <remarks>
/// <para>
/// <b>The id is supplied by the caller, not generated here.</b> Stock's reservations carry an
/// <c>order_id</c> and are created before the order row exists, so the id has to be known before
/// <see cref="Create"/> runs. It is also the seed for <see cref="OrderNumber"/>, which is why the
/// number needs no sequence.
/// </para>
/// <para>
/// <b>An order is write-once in Phase 0.</b> The status is decided before the single
/// <c>SaveChangesAsync</c>, so <see cref="PlacedAtUtc"/> is the only timestamp and there is
/// deliberately no <c>UpdatedAtUtc</c> — a column that could only ever equal it would imply an
/// update path that does not exist. Phase 2's saga adds both the path and the column.
/// </para>
/// <para>
/// For the same reason the <c>xmin</c> token is currently inert: nothing issues an UPDATE against
/// this row. It is declared anyway, because optimistic concurrency is mandatory on every entity
/// and Phase 2 will need it.
/// </para>
/// </remarks>
public class Order
{
    /// <summary>
    /// Sized for <c>ORD-yyyyMMdd-xxxxxxxx</c> (21 characters) with room for the format to grow.
    /// A generated number that no longer fits is a schema drift, so
    /// <c>ContractSchemaAlignmentTests</c> measures a real one against this.
    /// </summary>
    public const int OrderNumberMaxLength = 32;

    /// <summary>Bounds one order so a single request cannot produce unbounded work or an unbounded row.</summary>
    public const int MaxLines = 50;

    /// <summary>Mirrors Catalog's <c>Product.CurrencyLength</c>; declared here rather than shared.</summary>
    public const int CurrencyLength = 3;

    /// <summary>
    /// The largest total that fits <c>numeric(18, 2)</c>. Checked in the domain rather than left
    /// to PostgreSQL, because a database <c>22003</c> would report the failure as a bad value
    /// without saying which order caused it.
    /// </summary>
    public const decimal MaxTotalAmount = 9_999_999_999_999_999m;

    private const string OrderNumberPrefix = "ORD";
    private const string OrderNumberDateFormat = "yyyyMMdd";
    private const int OrderNumberSuffixLength = 8;

    private readonly List<OrderLine> _lines = [];

    /// <summary>EF Core materialisation only.</summary>
    private Order()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>
    /// The public reference. Immutable, and unique in the schema: a collision is a 23505 that the
    /// handler reports as a 409 rather than a silently duplicated reference.
    /// </summary>
    public string OrderNumber { get; private set; } = string.Empty;

    public OrderStatus Status { get; private set; }

    /// <summary>One currency per order, snapshotted from Catalog and agreed across every line.</summary>
    public string Currency { get; private set; } = string.Empty;

    /// <summary>
    /// Stored rather than derived. Every line total is already rounded, so this is their exact
    /// sum, and it cannot drift: an order is write-once, so the inputs never change after
    /// construction. Storing it is what lets the headline number be read without materialising
    /// the collection.
    /// </summary>
    public decimal TotalAmount { get; private set; }

    public DateTimeOffset PlacedAtUtc { get; private set; }

    public IReadOnlyCollection<OrderLine> Lines => _lines;

    /// <summary>Derived from <see cref="Status"/>; not a column.</summary>
    public bool IsTerminal => Status != OrderStatus.Pending;

    public static Order Create(Guid id, string currency, IReadOnlyList<OrderLine> lines)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Order id is required.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lines.Count, nameof(lines));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lines.Count, MaxLines, nameof(lines));

        // Normalised before it is measured, so the bound applies to the value that will be stored
        // rather than to whatever spacing the caller happened to send. Catalog normalises in the
        // same order; a snapshot that disagreed with its source by a trim would be a second truth.
        var normalizedCurrency = currency.Trim().ToUpperInvariant();
        ArgumentOutOfRangeException.ThrowIfGreaterThan(normalizedCurrency.Length, CurrencyLength, nameof(currency));

        // Guards complete before anything is constructed, so a rejected call leaves no partial
        // aggregate behind.
        var seen = new HashSet<Guid>(lines.Count);
        var total = 0m;

        foreach (var line in lines)
        {
            ArgumentNullException.ThrowIfNull(line, nameof(lines));

            if (line.OrderId != id)
            {
                throw new ArgumentException(
                    $"Line {line.Id} belongs to order {line.OrderId}, not {id}.", nameof(lines));
            }

            // One line per product. The placement path combines duplicates before getting here,
            // because Stock's UNIQUE(order_id, stock_item_id) would reject the second hold.
            if (!seen.Add(line.ProductId))
            {
                throw new ArgumentException(
                    $"Product {line.ProductId} appears on more than one line.", nameof(lines));
            }

            total += line.LineTotal;
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(total, MaxTotalAmount, nameof(lines));

        var placedAtUtc = DateTimeOffset.UtcNow;

        var order = new Order
        {
            Id = id,
            OrderNumber = NewOrderNumber(id, placedAtUtc),
            Status = OrderStatus.Pending,
            Currency = normalizedCurrency,
            TotalAmount = total,
            PlacedAtUtc = placedAtUtc
        };

        order._lines.AddRange(lines);

        return order;
    }

    /// <summary>Settles the order as fulfilled: every line was reserved and confirmed.</summary>
    public void Confirm() => TransitionTo(OrderStatus.Confirmed);

    /// <summary>Settles the order as unfulfilled: nothing was confirmed.</summary>
    public void Fail() => TransitionTo(OrderStatus.Failed);

    /// <summary>
    /// Settles the order as needing reconciliation: some lines were confirmed and could not be
    /// undone, or the outcome is unknown because the reconciliation read failed.
    /// </summary>
    public void MarkPartiallyConfirmed() => TransitionTo(OrderStatus.PartiallyConfirmed);

    /// <summary>
    /// <c>ORD-yyyyMMdd-xxxxxxxx</c>: sortable by day, human-referenceable, and derived entirely
    /// from immutable values, so it needs no sequence and no database round trip. A sequence
    /// would also make the test fixture order-dependent, because truncating rows does not reset
    /// one — see <c>../../../docs/KNOWN-ISSUES.md</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="CultureInfo.InvariantCulture"/> is not optional. This host is Turkish-locale and
    /// a culture-sensitive format has already leaked a server-formatted value into a response once.
    /// </remarks>
    private static string NewOrderNumber(Guid id, DateTimeOffset placedAtUtc)
        => $"{OrderNumberPrefix}-{placedAtUtc.ToString(OrderNumberDateFormat, CultureInfo.InvariantCulture)}" +
           $"-{id.ToString("N")[..OrderNumberSuffixLength]}";

    /// <summary>
    /// Strict, and only from <see cref="OrderStatus.Pending"/>. Reaching the throw means this
    /// assembly transitioned the same order twice: no caller input can do it, because Phase 0
    /// exposes no endpoint that mutates a persisted order. It is therefore an
    /// <see cref="InvalidOperationException"/> — a 500 — rather than a domain 409. Reporting our
    /// own double-transition as the caller's conflict would hide the bug inside their error budget,
    /// which is the same reasoning Stock applies to a CHECK violation.
    /// </summary>
    private void TransitionTo(OrderStatus target)
    {
        if (Status != OrderStatus.Pending)
        {
            throw new InvalidOperationException(
                $"Order {Id} is already {Status} and cannot become {target}.");
        }

        Status = target;
    }
}
