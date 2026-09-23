namespace AgenticShop.Stock.Domain;

/// <summary>
/// Thrown when a reservation cannot be honoured. Carries the numbers as typed properties so
/// the exception handler can build a client-facing message from a controlled vocabulary —
/// <see cref="Exception.Message"/> is for logs only and never reaches the caller.
/// </summary>
/// <remarks>
/// A distinct type is required because the handler maps <see cref="ArgumentException"/> to
/// 400, and insufficient stock is a 409: it is a conflict with the resource's current state
/// that a retry after restocking could resolve, not a malformed request.
/// </remarks>
public sealed class InsufficientStockException(int available, int requested)
    : Exception($"Requested {requested} unit(s) but only {available} are available.")
{
    public int Available { get; } = available;

    public int Requested { get; } = requested;
}
