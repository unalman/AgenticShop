using System.Net;
using System.Net.Http.Json;

namespace AgenticShop.Ordering.Clients;

/// <summary>
/// Typed client for Stock. Registered with a base address from <c>Services:Stock:BaseUrl</c>.
/// </summary>
/// <remarks>
/// Every method here reports refusals as a value and faults as an exception, and never reads
/// Stock's <c>detail</c> text. That is what keeps the placement path honest: a 409 on confirm means
/// "not settled by this call" and nothing more, so the caller reads the authoritative state back
/// instead of guessing which 409 it was.
/// </remarks>
public sealed class StockClient(HttpClient http) : DownstreamClient(http, "Stock"), IStockClient
{
    public async Task<Guid?> ReserveAsync(
        Guid productId,
        Guid orderId,
        int quantity,
        CancellationToken cancellationToken)
    {
        const string operation = nameof(ReserveAsync);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/stock/{productId}/reservations")
        {
            Content = JsonContent.Create(new ReserveBody(orderId, quantity))
        };

        // This is the one outbound call that must not be retried, and the reason is the 409 branch
        // below: a retry after a first attempt that committed but whose 201 was lost would come back
        // 409 from Stock's UNIQUE(order_id, stock_item_id), and this method would report it as a
        // refusal. See DownstreamResilience and docs/DECISIONS.md → O18.
        request.Options.Set(DownstreamResilience.NotRetryable, true);

        using var response = await SendAsync(request, operation, cancellationToken);

        // 404: Stock has no record for that product. 409: not enough available, or that order
        // already holds it — which cannot happen here, because placement combines duplicate
        // product lines, mints a fresh order id, and never retries this call. Both mean "this line
        // cannot be held".
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
        {
            return null;
        }

        if (response.StatusCode == HttpStatusCode.Created)
        {
            var reservation = await response.Content.ReadFromJsonAsync<ReservationBody>(cancellationToken);

            if (reservation is null || reservation.Id == Guid.Empty)
            {
                throw MalformedBody(Service, operation);
            }

            return reservation.Id;
        }

        throw Unexpected(Service, operation, response);
    }

    public Task<bool> ConfirmAsync(Guid reservationId, CancellationToken cancellationToken)
        => SettleAsync(reservationId, "confirm", nameof(ConfirmAsync), cancellationToken);

    public Task<bool> ReleaseAsync(Guid reservationId, CancellationToken cancellationToken)
        => SettleAsync(reservationId, "release", nameof(ReleaseAsync), cancellationToken);

    public async Task<IReadOnlyList<StockReservationSnapshot>> ListByOrderAsync(
        Guid orderId,
        CancellationToken cancellationToken)
    {
        const string operation = nameof(ListByOrderAsync);

        using var response = await SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/api/v1/reservations?orderId={orderId}"),
            operation,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw Unexpected(Service, operation, response);
        }

        var rows = await response.Content.ReadFromJsonAsync<List<ReservationBody>>(cancellationToken);

        if (rows is null)
        {
            throw MalformedBody(Service, operation);
        }

        // A row without a status would classify as neither confirmed nor pending, and would
        // therefore be left holding stock without anyone noticing. Refuse the whole read instead.
        if (rows.Any(row => string.IsNullOrWhiteSpace(row.Status)))
        {
            throw MalformedBody(Service, operation);
        }

        return rows
            .Select(row => new StockReservationSnapshot(row.Id, row.ProductId, row.Quantity, row.Status))
            .ToList();
    }

    private async Task<bool> SettleAsync(
        Guid reservationId,
        string action,
        string operation,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            new HttpRequestMessage(HttpMethod.Post, $"/api/v1/reservations/{reservationId}/{action}"),
            operation,
            cancellationToken);

        // 409 covers "already settled" and an xmin conflict; 404 covers a reservation that is not
        // there. Neither is a fault, and neither is a success — the caller reconciles.
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
        {
            return false;
        }

        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        throw Unexpected(Service, operation, response);
    }

    /// <summary>Shaped to Stock's <c>ReserveStockRequest</c>. Ordering owns its own copy.</summary>
    private sealed record ReserveBody(Guid OrderId, int Quantity);

    /// <summary>Shaped to the subset of Stock's <c>ReservationResponse</c> that Ordering reads.</summary>
    private sealed record ReservationBody(Guid Id, Guid ProductId, int Quantity, string Status);
}
