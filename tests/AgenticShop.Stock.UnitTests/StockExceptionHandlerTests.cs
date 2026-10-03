using System.Text.Json;
using AgenticShop.Stock.Data;
using AgenticShop.Stock.Domain;
using AgenticShop.Stock.Errors;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AgenticShop.Stock.UnitTests;

/// <summary>
/// The classification rules are tested here rather than only through the API because several
/// cannot be provoked over HTTP without a race or a corrupted database: an xmin conflict needs
/// two writers, and a CHECK violation needs the domain guards to have failed first.
/// </summary>
public class StockExceptionHandlerTests
{
    private const string RequestPath = "/api/v1/stock";
    private const string CorrelationId = "correlation-under-test";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task InsufficientStock_Returns409WithBothNumbers()
    {
        var outcome = await HandleAsync(new InsufficientStockException(available: 4, requested: 9));

        outcome.Status.Should().Be(409);
        outcome.Problem!.Title.Should().Be("Conflict.");
        outcome.Problem.Detail.Should().Be("Only 4 unit(s) are available; 9 were requested.");
    }

    [Fact]
    public async Task InsufficientStock_BuildsItsDetailFromTypedValuesNotFromTheMessage()
    {
        // Message is for logs. If the handler ever switched to returning it, a reworded
        // exception would silently change the public contract.
        var outcome = await HandleAsync(new InsufficientStockException(0, 1));

        outcome.Problem!.Detail.Should().NotBe(new InsufficientStockException(0, 1).Message);
        outcome.Problem.Detail.Should().Contain("0 unit(s) are available");
    }

    [Theory]
    [InlineData(ReservationStatus.Confirmed, ReservationStatus.Confirmed,
        "The reservation is already confirmed and cannot be confirmed.")]
    [InlineData(ReservationStatus.Confirmed, ReservationStatus.Released,
        "The reservation is already confirmed and cannot be released.")]
    [InlineData(ReservationStatus.Released, ReservationStatus.Confirmed,
        "The reservation is already released and cannot be confirmed.")]
    public async Task AnIllegalReservationTransition_Returns409NamingBothStates(
        ReservationStatus current,
        ReservationStatus attempted,
        string expectedDetail)
    {
        var outcome = await HandleAsync(new InvalidReservationStateException(current, attempted));

        outcome.Status.Should().Be(409);
        outcome.Problem!.Detail.Should().Be(expectedDetail);
    }

    [Fact]
    public async Task ConcurrencyConflict_Returns409WithAReloadHint()
    {
        var outcome = await HandleAsync(new DbUpdateConcurrencyException("The row was changed."));

        outcome.Status.Should().Be(409);
        outcome.Problem!.Title.Should().Be("Conflict.");
        outcome.Problem.Detail.Should().Contain("another request");
        outcome.Problem.Detail.Should().Contain("stock", "the wording must be Stock's, not a copy of Catalog's");
    }

    [Fact]
    public async Task DuplicateStockRecord_NamesTheProduct()
    {
        var outcome = await HandleAsync(
            UniqueViolation("stock_items", StockItemConfiguration.UniqueProductIndexName));

        outcome.Status.Should().Be(409);
        outcome.Problem!.Detail.Should().Be("A stock record for that product already exists.");
    }

    [Fact]
    public async Task DuplicateReservationForTheSameOrder_ExplainsTheIdempotencyGuard()
    {
        var outcome = await HandleAsync(
            UniqueViolation("stock_reservations", StockReservationConfiguration.UniqueOrderStockItemIndexName));

        outcome.Status.Should().Be(409);
        outcome.Problem!.Detail.Should().Be("That order already has a reservation for this product.");
    }

    [Fact]
    public async Task AUniqueViolationOnSomeOtherConstraint_BlamesNeither()
    {
        // Keyed on the constraint name, so a third unique index cannot be misreported as one
        // of the two we know about.
        var outcome = await HandleAsync(UniqueViolation("stock_items", "ix_some_future_index"));

        outcome.Status.Should().Be(409);
        outcome.Problem!.Detail.Should().Be("The value conflicts with an existing record.");
        outcome.Problem.Detail.Should().NotContain("stock record for that product");
        outcome.Problem.Detail.Should().NotContain("already has a reservation");
    }

    [Fact]
    public async Task ACheckConstraintViolation_IsAServerFaultNotACallerError()
    {
        // Pins a deliberate decision. Every CHECK on these tables restates an invariant
        // StockItem already guards, and xmin closes the concurrent path, so a violation means
        // this assembly has a bug. Reporting it as 409 would hide our own defect inside the
        // caller's error budget — and the rules run in both directions: a client error is never
        // a 5xx, and a server fault is never a 4xx.
        var outcome = await HandleAsync(
            new DbUpdateException(
                "An error occurred while saving the entity changes.",
                new PostgresException(
                    "new row for relation \"stock_items\" violates check constraint",
                    "ERROR",
                    "ERROR",
                    PostgresErrorCodes.CheckViolation)));

        outcome.Status.Should().Be(500);
        outcome.Problem!.Detail.Should().BeNull();

        var entry = outcome.Log.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Error, "a breached invariant is an incident");
        entry.Exception.Should().NotBeNull();
    }

    [Theory]
    [InlineData(PostgresErrorCodes.StringDataRightTruncation, "A submitted value is too long for its field.")]
    [InlineData(PostgresErrorCodes.NumericValueOutOfRange, "A submitted value is out of range for its field.")]
    public async Task AValueTheDatabaseWillNotFit_IsAClientError(string sqlState, string expectedDetail)
    {
        var outcome = await HandleAsync(new DbUpdateException(
            "An error occurred while saving.",
            new PostgresException("value does not fit", "ERROR", "ERROR", sqlState)));

        outcome.Status.Should().Be(400);
        outcome.Problem!.Detail.Should().Be(expectedDetail);
    }

    [Fact]
    public async Task DivergedCounters_AreAServerFault()
    {
        // StockItem.RequireCoveredByReserved throws this when Reserved has drifted from the
        // reservation rows. No caller input can produce it.
        var outcome = await HandleAsync(new InvalidOperationException(
            "Cannot settle 3 unit(s): only 1 are reserved."));

        outcome.Status.Should().Be(500);
        outcome.Problem!.Detail.Should().BeNull();
        outcome.RawBody.Should().NotContain("only 1 are reserved");
    }

    [Theory]
    [InlineData(400)]
    [InlineData(413)]
    public async Task BadHttpRequest_UsesTheStatusCodeTheFrameworkAlreadyChose(int statusCode)
    {
        var outcome = await HandleAsync(new BadHttpRequestException("Failed to bind.", statusCode));

        outcome.Handled.Should().BeTrue();
        outcome.Status.Should().Be(statusCode);
        outcome.Problem!.Title.Should().Be("Bad request.");
        outcome.Problem.Detail.Should().BeNull();
    }

    [Fact]
    public async Task ADomainGuardDoesNotLeakItsMessage()
    {
        var exception = new ArgumentException("Cannot set on-hand to 5 while 6 unit(s) are reserved.", "quantityOnHand");

        var outcome = await HandleAsync(exception);

        outcome.Status.Should().Be(400);
        outcome.Problem!.Detail.Should().BeNull();
        outcome.RawBody.Should().NotContain("quantityOnHand");
    }

    [Fact]
    public async Task ClientCancellation_IsHandedBackToTheFrameworkAndWritesNothing()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();

        var outcome = await HandleAsync(
            new OperationCanceledException("The operation was canceled.", aborted.Token),
            requestAborted: aborted.Token);

        outcome.Handled.Should().BeFalse();
        outcome.RawBody.Should().BeEmpty();
        outcome.Log.Entries.Should().ContainSingle().Subject.Level.Should().Be(LogLevel.Debug);
    }

    [Fact]
    public async Task AnInternalTimeoutIsNotMistakenForClientCancellation()
    {
        var outcome = await HandleAsync(new OperationCanceledException("internal timeout"));

        outcome.Handled.Should().BeTrue();
        outcome.Status.Should().Be(500);
    }

    [Fact]
    public async Task AHandledRejection_LogsOneWarningLineReachingTheRootCause()
    {
        var outcome = await HandleAsync(
            UniqueViolation("stock_reservations", StockReservationConfiguration.UniqueOrderStockItemIndexName));

        var entry = outcome.Log.Entries.Should().ContainSingle().Subject;

        entry.Level.Should().Be(LogLevel.Warning);
        entry.Exception.Should().BeNull("a handled rejection is not an incident, so it needs no stack trace");
        entry.Message.Should().Contain("->", "the chain must reach past EF's wrapper");
        entry.Message.Should().Contain(PostgresErrorCodes.UniqueViolation);
        entry.Message.Should().Contain("constraint=" + StockReservationConfiguration.UniqueOrderStockItemIndexName);
        entry.Message.Should().Contain("table=stock_reservations");
        entry.Message.Should().Contain(CorrelationId);
        entry.Message.Should().NotContain("\n");
        entry.Message.Should().NotContain("..", "the template must not double a terminal period");
    }

    [Fact]
    public async Task AConcurrencyConflict_LogsOneConciseLineInsteadOfEFsBoilerplate()
    {
        // EF's message for this exception is ~230 characters ending in a documentation URL, and in
        // one verified run 55 of 144 log lines were that message. There is no inner
        // PostgresException to reach, because a zero-rows-affected UPDATE is not a SQL failure — so
        // the chain walk was already correct and had nothing to find. Asserted in Stock alone, not
        // in all three suites: the description is shared infrastructure and one specification is
        // enough. Stock is the one place the volume was measured.
        var outcome = await HandleAsync(new DbUpdateConcurrencyException("The row was changed."));

        var entry = outcome.Log.Entries.Should().ContainSingle().Subject;

        entry.Level.Should().Be(LogLevel.Warning);
        entry.Exception.Should().BeNull("a handled rejection is not an incident, so it needs no stack trace");
        entry.Message.Should().Contain("DbUpdateConcurrencyException: no rows updated");
        entry.Message.Should().Contain("concurrency token did not match");
        entry.Message.Should().NotContain("https://", "EF's documentation URL is not diagnostic content");
        entry.Message.Should().NotContain("\n");
        entry.Message.Should()
            .NotContain("The row was changed.", "EF's own message must be replaced, not appended to");
    }

    [Fact]
    public async Task EveryHandledResponse_IsProblemJsonWithTheCorrelationId()
    {
        var outcome = await HandleAsync(new InvalidOperationException("boom"));

        outcome.ContentType.Should().Be("application/problem+json");
        outcome.Problem!.Instance.Should().Be(RequestPath);
        outcome.Problem.CorrelationId.Should().Be(CorrelationId);
    }

    [Fact]
    public async Task TheEnrichedLogStillLeavesNothingInTheResponse()
    {
        var outcome = await HandleAsync(
            UniqueViolation("stock_items", StockItemConfiguration.UniqueProductIndexName));

        outcome.Log.SingleMessage.Should().Contain(StockItemConfiguration.UniqueProductIndexName);

        outcome.RawBody.Should().NotContain(PostgresErrorCodes.UniqueViolation);
        outcome.RawBody.Should().NotContain(StockItemConfiguration.UniqueProductIndexName);
        outcome.RawBody.Should().NotContain("DbUpdateException");
        outcome.RawBody.Should().NotContain("PostgresException");
    }

    private static async Task<Outcome> HandleAsync(
        Exception exception,
        CancellationToken requestAborted = default)
    {
        var log = new CapturingLogger();
        var handler = new StockExceptionHandler(log);

        // ProblemHttpResult resolves ILoggerFactory and JsonOptions from the request
        // services, so a bare DefaultHttpContext is not enough.
        var services = new ServiceCollection().AddOptions().AddLogging().BuildServiceProvider();

        var context = new DefaultHttpContext
        {
            TraceIdentifier = CorrelationId,
            RequestAborted = requestAborted,
            RequestServices = services
        };

        context.Request.Method = "POST";
        context.Request.Path = RequestPath;

        var body = new MemoryStream();
        context.Response.Body = body;

        var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);

        body.Position = 0;
        var raw = await new StreamReader(body).ReadToEndAsync();

        return new Outcome(
            handled,
            context.Response.StatusCode,
            context.Response.ContentType,
            raw,
            raw.Length == 0 ? null : JsonSerializer.Deserialize<ProblemPayload>(raw, JsonOptions),
            log);
    }

    /// <summary>
    /// Records what the handler logged. The response and the log are separate contracts with
    /// opposite requirements: the response must leak nothing, the log must explain everything.
    /// </summary>
    private sealed class CapturingLogger : ILogger<StockExceptionHandler>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public string SingleMessage => Entries.Should().ContainSingle().Subject.Message;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }

    private static DbUpdateException UniqueViolation(string tableName, string constraintName) => new(
        "An error occurred while saving the entity changes.",
        // PostgresException exposes its error fields as read-only, so the constraint name can
        // only be supplied through the full constructor.
        new PostgresException(
            "duplicate key value violates unique constraint",
            "ERROR",
            "ERROR",
            PostgresErrorCodes.UniqueViolation,
            schemaName: "public",
            tableName: tableName,
            constraintName: constraintName));

    private sealed record Outcome(
        bool Handled,
        int Status,
        string? ContentType,
        string RawBody,
        ProblemPayload? Problem,
        CapturingLogger Log);

    private sealed record ProblemPayload
    {
        public int? Status { get; init; }

        public string? Title { get; init; }

        public string? Detail { get; init; }

        public string? Instance { get; init; }

        public string? CorrelationId { get; init; }
    }
}
