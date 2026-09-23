using AgenticShop.Stock.Contracts;
using AgenticShop.Stock.Data;
using AgenticShop.Stock.Domain;
using AgenticShop.Stock.Validation;
using Microsoft.EntityFrameworkCore;

namespace AgenticShop.Stock.Endpoints;

/// <summary>
/// The reservation lifecycle: hold, then settle as shipped or cancelled.
/// </summary>
/// <remarks>
/// Every write here mutates a <see cref="StockItem"/> and a <see cref="StockReservation"/>
/// inside one tracked <see cref="StockDbContext"/>, committed by a single
/// <c>SaveChangesAsync</c>. EF wraps that in one transaction, so the counter and the audit row
/// can never disagree — and it is the same property Phase 2's outbox will rely on, because an
/// outbox row added to this context would commit atomically with both.
/// </remarks>
public static class ReservationEndpoints
{
    public static IEndpointRouteBuilder MapReservationEndpoints(this IEndpointRouteBuilder app)
    {
        var holds = app.MapGroup("/api/v1/stock/{productId:guid}/reservations")
            .WithTags("Reservations")
            .AddEndpointFilter<DataAnnotationValidationFilter>();

        holds.MapPost("/", ReserveAsync)
            .WithName("ReserveStock")
            .WithSummary("Holds quantity for an order. 409 when there is not enough, or when that order already holds this product.")
            .Produces<ReservationResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        var reservations = app.MapGroup("/api/v1/reservations")
            .WithTags("Reservations")
            .AddEndpointFilter<DataAnnotationValidationFilter>();

        reservations.MapPost("/{reservationId:guid}/confirm", ConfirmAsync)
            .WithName("ConfirmReservation")
            .WithSummary("Settles a hold as shipped: on-hand and reserved both drop.")
            .Produces<ReservationResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        reservations.MapPost("/{reservationId:guid}/release", ReleaseAsync)
            .WithName("ReleaseReservation")
            .WithSummary("Settles a hold as cancelled: reserved drops, on-hand is untouched.")
            .Produces<ReservationResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        reservations.MapGet("/", ListByOrderAsync)
            .WithName("ListReservationsForOrder")
            .WithSummary("Lists every reservation for one order — the compensation and reconciliation path.")
            .Produces<List<ReservationResponse>>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return app;
    }

    private static async Task<IResult> ReserveAsync(
        Guid productId,
        ReserveStockRequest request,
        StockDbContext db,
        CancellationToken cancellationToken)
    {
        var item = await db.StockItems
            .FirstOrDefaultAsync(s => s.ProductId == productId, cancellationToken);

        if (item is null)
        {
            return Results.NotFound();
        }

        // Throws InsufficientStockException when the hold cannot be honoured, before anything
        // is mutated, so a rejection leaves the entity untouched.
        item.Reserve(request.Quantity);

        var reservation = StockReservation.Create(item.Id, request.OrderId, request.Quantity);

        db.StockReservations.Add(reservation);

        // One commit for the counter and the audit row. A duplicate (order, stock item) is
        // left to the unique index rather than pre-checked: a SELECT-then-INSERT test has a
        // race window under retry, the constraint does not. The handler turns it into a 409.
        await db.SaveChangesAsync(cancellationToken);

        return Results.Created(
            $"/api/v1/reservations/{reservation.Id}",
            ReservationResponse.From(reservation, item.ProductId));
    }

    private static async Task<IResult> ConfirmAsync(
        Guid reservationId,
        StockDbContext db,
        CancellationToken cancellationToken)
        => await SettleAsync(reservationId, db, cancellationToken, confirm: true);

    private static async Task<IResult> ReleaseAsync(
        Guid reservationId,
        StockDbContext db,
        CancellationToken cancellationToken)
        => await SettleAsync(reservationId, db, cancellationToken, confirm: false);

    private static async Task<IResult> SettleAsync(
        Guid reservationId,
        StockDbContext db,
        CancellationToken cancellationToken,
        bool confirm)
    {
        var reservation = await db.StockReservations
            .FirstOrDefaultAsync(r => r.Id == reservationId, cancellationToken);

        if (reservation is null)
        {
            return Results.NotFound();
        }

        var item = await db.StockItems
            .FirstOrDefaultAsync(s => s.Id == reservation.StockItemId, cancellationToken);

        // The foreign key is Restrict, so a reservation without its stock item cannot exist.
        // Reaching this means the schema or the data has been damaged outside the API, which
        // is a server fault and must not be reported as the caller's 404.
        if (item is null)
        {
            throw new InvalidOperationException(
                $"Reservation {reservationId} points at stock item {reservation.StockItemId}, which does not exist.");
        }

        // Transition the reservation first: if it has already settled, this throws and the
        // counters are never touched.
        if (confirm)
        {
            reservation.Confirm();
            item.ConfirmReservation(reservation.Quantity);
        }
        else
        {
            reservation.Release();
            item.ReleaseReservation(reservation.Quantity);
        }

        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(ReservationResponse.From(reservation, item.ProductId));
    }

    private static async Task<IResult> ListByOrderAsync(
        Guid orderId,
        StockDbContext db,
        CancellationToken cancellationToken)
    {
        // A join is legitimate here: both tables are in Stock's own database. What the
        // boundary forbids is a join across services, not one within a service.
        var rows = await (
            from reservation in db.StockReservations.AsNoTracking()
            join item in db.StockItems.AsNoTracking()
                on reservation.StockItemId equals item.Id
            where reservation.OrderId == orderId
            orderby reservation.CreatedAtUtc, reservation.Id
            select new
            {
                reservation.Id,
                reservation.StockItemId,
                item.ProductId,
                reservation.OrderId,
                reservation.Quantity,
                reservation.Status,
                reservation.CreatedAtUtc,
                reservation.UpdatedAtUtc
            })
            .ToListAsync(cancellationToken);

        // Status is converted to its name in memory: the enum-to-string mapping is a materialisation
        // concern, and projecting ToString() into SQL would depend on the provider translating it.
        var reservations = rows
            .Select(row => new ReservationResponse(
                row.Id,
                row.StockItemId,
                row.ProductId,
                row.OrderId,
                row.Quantity,
                row.Status.ToString(),
                row.CreatedAtUtc,
                row.UpdatedAtUtc))
            .ToList();

        return Results.Ok(reservations);
    }
}
