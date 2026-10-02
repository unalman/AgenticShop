using System.Text.Json;
using AgenticShop.Ordering.Clients;
using AgenticShop.Ordering.Data;
using AgenticShop.Ordering.Domain;
using AgenticShop.Ordering.Errors;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AgenticShop.Ordering.UnitTests;

/// <summary>
/// The classification rules are tested here rather than only through the API because several cannot
/// be provoked over HTTP without a race, a corrupted database or a dead dependency: an xmin conflict
/// needs two writers, a CHECK violation needs the domain guards to have failed first, and a 502 needs
/// Stock to disappear mid-request.
/// </summary>
public class OrderingExceptionHandlerTests
{
    private const string RequestPath = "/api/v1/orders";
    private const string CorrelationId = "correlation-under-test";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task AnUnorderableProduct_Returns409NamingIt()
    {
        var productId = Guid.NewGuid();

        var outcome = await HandleAsync(new ProductUnavailableException(productId));

        outcome.Status.Should().Be(409);
        outcome.Problem!.Title.Should().Be("Conflict.");
        outcome.Problem.Detail.Should().Be($"Product {productId} cannot be ordered.");
        outcome.Problem.OrderId.Should().BeNull("no order exists yet: nothing was reserved");
    }

    [Fact]
    public async Task AMixedCurrencyOrder_Returns400WithNoDetail()
    {
        // A 400 like every other domain 400 in the repository: detail is null, because invariant
        // three says no exception message reaches the client. The codes are on the exception's
        // typed property and reach the operator through the log line instead.
        var outcome = await HandleAsync(new MixedCurrencyException(["USD", "EUR"]));

        outcome.Status.Should().Be(400);
        outcome.Problem!.Title.Should().Be("Bad request.");
        outcome.Problem.Detail.Should().BeNull();
    }

    [Fact]
    public async Task AMixedCurrencyOrder_KeepsTheCurrenciesOutOfTheBodyAndInTheLog()
    {
        // Both halves of invariant three at once. The response must not name the currencies; the log
        // must, or "the request was invalid" is undiagnosable and the typed property is decorative.
        var outcome = await HandleAsync(new MixedCurrencyException(["USD", "EUR"]));

        outcome.RawBody.Should().NotContain("USD");
        outcome.RawBody.Should().NotContain("EUR");

        outcome.Log.Entries.Should().ContainSingle();
        outcome.Log.Entries[0].Level.Should().Be(LogLevel.Warning);
        outcome.Log.Entries[0].Exception.Should().BeNull("a handled rejection is not an incident");
        outcome.Log.SingleMessage.Should().Contain("USD").And.Contain("EUR");
    }

    [Fact]
    public void AMixedCurrencyExceptionStillCarriesItsTypedProperties()
    {
        // Nothing in the response is built from these any more, so this is the assertion that stops
        // the property being deleted as "unused" — it is what the log line is built from.
        var exception = new MixedCurrencyException(["USD", "EUR", "GBP"]);

        exception.Currencies.Should().Equal("USD", "EUR", "GBP");
        exception.Message.Should().Contain("USD").And.Contain("EUR").And.Contain("GBP");
    }

    [Fact]
    public async Task RefusedStock_Returns409AndEchoesTheOrderId()
    {
        var productId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        var outcome = await HandleAsync(new StockUnavailableException(productId, orderId));

        outcome.Status.Should().Be(409);
        outcome.Problem!.Detail.Should().Be($"Not enough stock is available for product {productId}.");

        // The row was written as Failed, so a client that cannot see the id cannot query it — and a
        // blind retry against a non-idempotent endpoint is exactly what that ambiguity causes.
        outcome.Problem.OrderId.Should().Be(orderId);
    }

    [Fact]
    public async Task AnOrderFailedByADownstreamFault_Returns502WithTheOrderId()
    {
        var orderId = Guid.NewGuid();

        var outcome = await HandleAsync(
            new OrderPlacementIncompleteException(orderId, OrderStatus.Failed));

        outcome.Status.Should().Be(502);
        outcome.Problem!.Title.Should().Be("Downstream failure.");
        outcome.Problem.Detail.Should().Be(
            "The request could not be completed because a downstream service did not respond.");
        outcome.Problem.OrderId.Should().Be(orderId);
    }

    [Fact]
    public async Task APartiallyConfirmedOrder_Returns502SayingReconciliationIsRequired()
    {
        // The status is the whole point of the fourth value: a body that said only "failed" would
        // invite a re-order that ships the already-confirmed lines a second time.
        var orderId = Guid.NewGuid();

        var outcome = await HandleAsync(
            new OrderPlacementIncompleteException(orderId, OrderStatus.PartiallyConfirmed));

        outcome.Status.Should().Be(502);
        outcome.Problem!.Detail.Should().Be("The order was partially confirmed and requires reconciliation.");
        outcome.Problem.OrderId.Should().Be(orderId);
    }

    [Fact]
    public async Task ADownstreamFailure_DoesNotNameTheServiceThatFailed()
    {
        // The service name is typed onto the exception for the log line and must stop there: naming
        // our dependencies in a response body discloses internal topology to any caller.
        var outcome = await HandleAsync(
            new DownstreamServiceException("Stock", "ConfirmAsync", statusCode: 503, innerException: null));

        outcome.Status.Should().Be(502);
        outcome.Problem!.Detail.Should().NotContain("Stock");
        outcome.Problem.Detail.Should().NotContain("ConfirmAsync");
        outcome.RawBody.Should().NotContain("Stock");
        outcome.Problem.OrderId.Should().BeNull("nothing was written: Catalog failed before any hold");
    }

    [Fact]
    public async Task ADownstreamFailureStillLogsWhichServiceFailed()
    {
        // The response leaks nothing; the log explains everything. Those are separate contracts with
        // opposite requirements, and the correlation id is what joins them. A 5xx is logged with the
        // exception object attached rather than with a formatted reason, so the service name is on
        // the exception and reaches the log through it.
        var exception = new DownstreamServiceException(
            "Catalog", "GetProductAsync", statusCode: null, innerException: null);

        var outcome = await HandleAsync(exception);

        outcome.Log.Entries.Should().ContainSingle();
        outcome.Log.Entries[0].Level.Should().Be(LogLevel.Error);
        outcome.Log.Entries[0].Exception.Should().BeSameAs(exception);
        ((DownstreamServiceException)outcome.Log.Entries[0].Exception!).Service.Should().Be("Catalog");
        outcome.Log.SingleMessage.Should().Contain(CorrelationId);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(413)]
    [InlineData(414)]
    public async Task ABindingFailure_UsesTheStatusTheFrameworkChose(int statusCode)
    {
        // Never hardcoded to 400: an oversized body is a 413, and hardcoding would reintroduce the
        // same class of bug this arm exists to fix.
        var outcome = await HandleAsync(new BadHttpRequestException("Malformed request.", statusCode));

        outcome.Status.Should().Be(statusCode);
        outcome.Problem!.Title.Should().Be("Bad request.");
        outcome.Problem.Detail.Should().BeNull("a binding failure's message describes our own binding");
    }

    [Fact]
    public async Task AConcurrencyConflict_Returns409InThisServiceWords()
    {
        var outcome = await HandleAsync(new DbUpdateConcurrencyException("The row was changed."));

        outcome.Status.Should().Be(409);
        outcome.Problem!.Detail.Should().Contain("order");
        outcome.Problem.Detail.Should().NotContain("stock", "the wording must be Ordering's, not a copy of Stock's");
        outcome.Problem.Detail.Should().NotContain("product", "and not a copy of Catalog's either");
    }

    [Fact]
    public async Task ADuplicateOrderNumber_SaysSo()
    {
        var outcome = await HandleAsync(UniqueViolation("orders", OrderConfiguration.UniqueOrderNumberIndexName));

        outcome.Status.Should().Be(409);
        outcome.Problem!.Detail.Should().Be("That order number already exists.");
    }

    [Fact]
    public async Task ASecondLineForTheSameProduct_ExplainsTheSchemaRule()
    {
        var outcome = await HandleAsync(
            UniqueViolation("order_lines", OrderLineConfiguration.UniqueOrderProductIndexName));

        outcome.Status.Should().Be(409);
        outcome.Problem!.Detail.Should().Be("That order already has a line for this product.");
    }

    [Fact]
    public async Task AnUnrecognisedUniqueConstraint_FallsBackRatherThanMisattributing()
    {
        // The fallback arm matters as much as the named ones: a third unique index added without a
        // matching case must not report one of the other two causes.
        var outcome = await HandleAsync(UniqueViolation("orders", "ix_orders_something_new"));

        outcome.Status.Should().Be(409);
        outcome.Problem!.Detail.Should().Be("The value conflicts with an existing record.");
    }

    [Theory]
    [InlineData(PostgresErrorCodes.StringDataRightTruncation, "too long")]
    [InlineData(PostgresErrorCodes.NumericValueOutOfRange, "out of range")]
    public async Task AValueThatDoesNotFitItsColumn_IsACallerError(string sqlState, string expectedFragment)
    {
        var outcome = await HandleAsync(DataError("orders", sqlState));

        outcome.Status.Should().Be(400);
        outcome.Problem!.Detail.Should().Contain(expectedFragment);
    }

    [Fact]
    public async Task ADomainGuardRejection_Returns400WithNoDetail()
    {
        var outcome = await HandleAsync(
            new ArgumentException("unitPrice ('-5,00') must be a non-negative value.", "unitPrice"));

        outcome.Status.Should().Be(400);

        // Observed live in Catalog: an ArgumentException.Message carries internal parameter names
        // and server-culture formatting. Logged, never returned.
        outcome.Problem!.Detail.Should().BeNull();
        outcome.RawBody.Should().NotContain("unitPrice");
        outcome.RawBody.Should().NotContain("-5,00");
        outcome.Log.SingleMessage.Should().Contain("unitPrice");
    }

    [Fact]
    public async Task AnIllegalOrderTransition_IsAServerFaultNotACallerError()
    {
        // Order throws InvalidOperationException on a second transition. No caller input can reach
        // it — Phase 0 exposes no route that mutates a persisted order — so it can only mean this
        // assembly transitioned twice. Reporting it as a 409 would hide our own defect inside the
        // caller's error budget. No handler arm was added; the default arm is the correct answer.
        var outcome = await HandleAsync(new InvalidOperationException("Order X is already Confirmed."));

        outcome.Status.Should().Be(500);
        outcome.Problem!.Detail.Should().BeNull();
        outcome.RawBody.Should().NotContain("already Confirmed");
        outcome.Log.Entries.Should().ContainSingle();
        outcome.Log.Entries[0].Level.Should().Be(LogLevel.Error);
    }

    [Fact]
    public async Task ACheckConstraintViolation_IsAServerFaultNotACallerError()
    {
        // Same reasoning, and the same deliberate absence of an arm as in Catalog and Stock: every
        // CHECK restates an invariant the entity already guards and xmin closes the concurrent path,
        // so reaching one means this assembly has a bug.
        var outcome = await HandleAsync(DataError("orders", PostgresErrorCodes.CheckViolation));

        outcome.Status.Should().Be(500);
        outcome.Problem!.Detail.Should().BeNull();
        outcome.Log.Entries[0].Level.Should().Be(LogLevel.Error);
        outcome.Log.Entries[0].Exception.Should().NotBeNull();
    }

    [Fact]
    public async Task ClientCancellation_IsNotHandledAndIsNotAnError()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();

        var outcome = await HandleAsync(
            new OperationCanceledException("The operation was canceled.", null, aborted.Token),
            requestAborted: aborted.Token);

        outcome.Handled.Should().BeFalse("handed back to the framework: there is no client left to answer");
        outcome.Log.Entries.Should().ContainSingle();
        outcome.Log.Entries[0].Level.Should().Be(LogLevel.Debug);
    }

    [Fact]
    public async Task AnInternalTimeoutIsAFault_NotACancellation()
    {
        // The cancellation arm is guarded on RequestAborted precisely so this one still counts.
        var outcome = await HandleAsync(new OperationCanceledException("Timed out internally."));

        outcome.Handled.Should().BeTrue();
        outcome.Status.Should().Be(500);
        outcome.Log.Entries[0].Level.Should().Be(LogLevel.Error);
    }

    [Fact]
    public async Task EveryHandledResponseIsProblemJsonCarryingTheCorrelationId()
    {
        var outcome = await HandleAsync(new ProductUnavailableException(Guid.NewGuid()));

        outcome.ContentType.Should().Be("application/problem+json");
        outcome.Problem!.Instance.Should().Be(RequestPath);
        outcome.Problem.CorrelationId.Should().Be(CorrelationId);
    }

    [Fact]
    public async Task AHandledRejectionLogsOneWarningWithNoExceptionObject()
    {
        // Invariant four: the handler decides severity. A stack trace for a routine 409 is what made
        // Stock's contention bursts look like incidents.
        var outcome = await HandleAsync(new ProductUnavailableException(Guid.NewGuid()));

        outcome.Log.Entries.Should().ContainSingle();
        outcome.Log.Entries[0].Level.Should().Be(LogLevel.Warning);
        outcome.Log.Entries[0].Exception.Should().BeNull();
        outcome.Log.SingleMessage.Should().Contain("409");
    }

    [Fact]
    public async Task AnUnhandledFailureLogsErrorWithTheExceptionAttached()
    {
        var inner = new PostgresException("boom", "ERROR", "ERROR", PostgresErrorCodes.CheckViolation);
        var outer = new DbUpdateException("An error occurred while saving.", inner);

        var outcome = await HandleAsync(outer);

        outcome.Status.Should().Be(500);
        outcome.Log.Entries.Should().ContainSingle();
        outcome.Log.Entries[0].Level.Should().Be(LogLevel.Error);

        // The object is attached rather than formatted into the message, so the whole chain
        // survives: EF's wrapper on the outside and the PostgreSQL error that explains it inside.
        outcome.Log.Entries[0].Exception.Should().BeSameAs(outer);
        outcome.Log.Entries[0].Exception!.InnerException.Should().BeSameAs(inner);
    }

    [Fact]
    public async Task TheLogWalksToThePostgresErrorThatActuallyExplainsIt()
    {
        // EF's own message is "see the inner exception for details", which reports nothing. The
        // SQLSTATE and constraint name are what an operator triages on.
        var outcome = await HandleAsync(
            UniqueViolation("order_lines", OrderLineConfiguration.UniqueOrderProductIndexName));

        outcome.Log.SingleMessage.Should().Contain(PostgresErrorCodes.UniqueViolation);
        outcome.Log.SingleMessage.Should().Contain("ix_order_lines_order_id_product_id");
        outcome.Log.SingleMessage.Should().NotContain("see the inner exception");
    }

    private static async Task<Outcome> HandleAsync(
        Exception exception,
        CancellationToken requestAborted = default)
    {
        var log = new CapturingLogger();
        var handler = new OrderingExceptionHandler(log);

        // ProblemHttpResult resolves ILoggerFactory and JsonOptions from the request services, so a
        // bare DefaultHttpContext is not enough.
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
    private sealed class CapturingLogger : ILogger<OrderingExceptionHandler>
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

    private static DbUpdateException UniqueViolation(string tableName, string constraintName)
        => new("An error occurred while saving the entity changes.",
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

    private static DbUpdateException DataError(string tableName, string sqlState)
        => new("An error occurred while saving the entity changes.",
            new PostgresException(
                "value violates a constraint",
                "ERROR",
                "ERROR",
                sqlState,
                schemaName: "public",
                tableName: tableName));

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

        /// <summary>The Ordering-specific extension: which order a failed placement did write.</summary>
        public Guid? OrderId { get; init; }
    }
}
