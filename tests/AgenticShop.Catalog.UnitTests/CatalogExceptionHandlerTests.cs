using System.Text.Json;
using AgenticShop.Catalog.Errors;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AgenticShop.Catalog.UnitTests;

/// <summary>
/// The classification rules are tested here rather than only through the API because
/// several of them cannot be provoked over HTTP without a race or a broken database:
/// a concurrency conflict needs two writers, and a 413 needs an oversized body.
/// </summary>
public class CatalogExceptionHandlerTests
{
    private const string RequestPath = "/api/v1/products";
    private const string CorrelationId = "correlation-under-test";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(400)]
    [InlineData(413)]
    public async Task BadHttpRequest_UsesTheStatusCodeTheFrameworkAlreadyChose(int statusCode)
    {
        // H1: malformed JSON, an absent body, an unconvertible value and a non-numeric
        // query parameter all arrive as BadHttpRequestException. It carries the status the
        // framework wants, so read it instead of assuming 400 — an oversized body is a 413.
        var outcome = await HandleAsync(new BadHttpRequestException("Failed to bind.", statusCode));

        outcome.Handled.Should().BeTrue();
        outcome.Status.Should().Be(statusCode);
        outcome.Problem!.Title.Should().Be("Bad request.");
    }

    [Fact]
    public async Task ClientCancellation_IsHandedBackToTheFrameworkAndWritesNothing()
    {
        // M2: an aborted request is ordinary traffic. Returning false means it is neither
        // logged as an error nor converted into a 5xx that would page someone.
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();

        var outcome = await HandleAsync(
            new OperationCanceledException("The operation was canceled.", aborted.Token),
            requestAborted: aborted.Token);

        outcome.Handled.Should().BeFalse();
        outcome.RawBody.Should().BeEmpty();
        outcome.Status.Should().Be(200, "no response should have been written");
    }

    [Fact]
    public async Task AnInternalTimeoutIsNotMistakenForClientCancellation()
    {
        // The cancellation arm is guarded on RequestAborted. Without that guard a genuine
        // internal timeout would be silently discarded instead of reported as a fault.
        var outcome = await HandleAsync(new OperationCanceledException("internal timeout"));

        outcome.Handled.Should().BeTrue();
        outcome.Status.Should().Be(500);
    }

    [Fact]
    public async Task ConcurrencyConflict_Returns409WithAReloadHint()
    {
        var outcome = await HandleAsync(new DbUpdateConcurrencyException("The row was changed."));

        outcome.Status.Should().Be(409);
        outcome.Problem!.Title.Should().Be("Conflict.");
        outcome.Problem.Detail.Should().Contain("another request");
    }

    [Fact]
    public async Task DuplicateSku_NamesTheSkuInTheMessage()
    {
        var outcome = await HandleAsync(UniqueViolation("ix_products_sku"));

        outcome.Status.Should().Be(409);
        outcome.Problem!.Detail.Should().Be("A product with that SKU already exists.");
    }

    [Fact]
    public async Task AUniqueViolationOnSomeOtherConstraint_DoesNotBlameTheSku()
    {
        // M5: the message is keyed on the constraint name. Hardcoding "duplicate SKU" would
        // misreport the cause the moment a second unique index is added to the table.
        var outcome = await HandleAsync(UniqueViolation("ix_products_some_future_index"));

        outcome.Status.Should().Be(409);
        outcome.Problem!.Detail.Should().Be("The value conflicts with an existing record.");
        outcome.Problem.Detail.Should().NotContain("SKU");
    }

    [Theory]
    [InlineData(PostgresErrorCodes.StringDataRightTruncation, "A submitted value is too long for its field.")]
    [InlineData(PostgresErrorCodes.NumericValueOutOfRange, "A submitted value is out of range for its field.")]
    public async Task AValueTheDatabaseWillNotFit_IsAClientErrorNotAServerFault(
        string sqlState,
        string expectedDetail)
    {
        // L20: DataAnnotations normally stop these first, so this arm is defence in depth for
        // the day a column is added without a matching limit on the contract. Without it the
        // caller gets a 500 for what is unambiguously their input.
        var outcome = await HandleAsync(DataError(sqlState));

        outcome.Status.Should().Be(400);
        outcome.Problem!.Title.Should().Be("Bad request.");
        outcome.Problem.Detail.Should().Be(expectedDetail);
    }

    [Fact]
    public async Task ADatabaseErrorThatIsNotAConstraintViolation_IsStillA500()
    {
        var outcome = await HandleAsync(new DbUpdateException(
            "An error occurred while saving.",
            new PostgresException("connection reset", "FATAL", "FATAL", PostgresErrorCodes.ConnectionFailure)));

        outcome.Status.Should().Be(500);
        outcome.Problem!.Detail.Should().BeNull();
    }

    [Fact]
    public async Task ADomainGuardDoesNotLeakItsMessage()
    {
        // M7: the raw message carries internal parameter names and is formatted with the
        // server's culture — on this machine it rendered -5.00 as "-5,00". It belongs in
        // the log, not in the response.
        var exception = new ArgumentOutOfRangeException("price", -5m, "must be a non-negative value");

        var outcome = await HandleAsync(exception);

        outcome.Status.Should().Be(400);
        outcome.Problem!.Title.Should().Be("Bad request.");
        outcome.Problem.Detail.Should().BeNull();
    }

    [Fact]
    public async Task AnUnknownException_Returns500WithNoDetail()
    {
        var outcome = await HandleAsync(new InvalidOperationException("connection string is broken"));

        outcome.Status.Should().Be(500);
        outcome.Problem!.Title.Should().Be("An unexpected error occurred.");
        outcome.Problem.Detail.Should().BeNull();
        outcome.Problem.Detail.Should().NotContain("connection string");
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
    public async Task AHandledConflict_LogsASingleWarningLineReachingThePostgresRootCause()
    {
        var outcome = await HandleAsync(UniqueViolation("ix_products_sku"));

        var entry = outcome.Log.Entries.Should().ContainSingle().Subject;

        entry.Level.Should().Be(LogLevel.Warning);
        entry.Exception.Should().BeNull("a handled rejection is not an incident, so it needs no stack trace");

        // What used to be logged was EF's wrapper alone — "An error occurred while saving the
        // entity changes. See the inner exception for details." — which explains nothing.
        entry.Message.Should().Contain("->", "the chain must reach past EF's wrapper");
        entry.Message.Should().Contain(PostgresErrorCodes.UniqueViolation);
        entry.Message.Should().Contain("duplicate key value violates unique constraint");
        entry.Message.Should().Contain("constraint=ix_products_sku");
        entry.Message.Should().Contain("table=products");
        entry.Message.Should().Contain(CorrelationId);

        entry.Message.Should().NotContain("\n", "a handled rejection is one line, not a stack trace");
    }

    [Fact]
    public async Task TheLogMessageHasNoDuplicatedTerminalPeriod()
    {
        var outcome = await HandleAsync(UniqueViolation("ix_products_sku"));

        outcome.Log.SingleMessage.Should().NotContain("..");
    }

    [Fact]
    public async Task ABindingFailure_LogsBothTheParameterAndTheJsonRootCause()
    {
        // The outer message names the parameter that failed; only the inner one says why.
        // Logging either alone loses half the diagnosis.
        var exception = new BadHttpRequestException(
            "Failed to read parameter \"request\" from the request body as JSON.",
            400,
            new JsonException("Expected depth to be zero at the end of the JSON payload."));

        var outcome = await HandleAsync(exception);

        outcome.Log.Entries.Single().Level.Should().Be(LogLevel.Warning);
        outcome.Log.SingleMessage.Should().Contain("\"request\"");
        outcome.Log.SingleMessage.Should().Contain("Expected depth to be zero");
    }

    [Fact]
    public async Task AnUnhandledFailure_LogsAtErrorWithTheExceptionAttached()
    {
        // The counterpart to silencing Microsoft.EntityFrameworkCore.Update: genuinely
        // unhandled failures must still be recorded at Error, with the exception object so
        // the stack trace and inner exceptions survive.
        var exception = new InvalidOperationException("connection string is broken");

        var outcome = await HandleAsync(exception);

        var entry = outcome.Log.Entries.Should().ContainSingle().Subject;

        entry.Level.Should().Be(LogLevel.Error);
        entry.Exception.Should().BeSameAs(exception);
    }

    [Fact]
    public async Task TheEnrichedLogStillLeavesNothingInTheResponse()
    {
        // The log gained diagnostic detail; the client must not have inherited any of it.
        var outcome = await HandleAsync(UniqueViolation("ix_products_sku"));

        outcome.Log.SingleMessage.Should().Contain("ix_products_sku");

        outcome.Problem!.Detail.Should().Be("A product with that SKU already exists.");
        outcome.RawBody.Should().NotContain(PostgresErrorCodes.UniqueViolation);
        outcome.RawBody.Should().NotContain("ix_products_sku");
        outcome.RawBody.Should().NotContain("DbUpdateException");
        outcome.RawBody.Should().NotContain("PostgresException");
    }

    [Fact]
    public async Task ClientCancellation_LogsAtDebugAndNotAsARejection()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();

        var outcome = await HandleAsync(
            new OperationCanceledException("The operation was canceled.", aborted.Token),
            requestAborted: aborted.Token);

        var entry = outcome.Log.Entries.Should().ContainSingle().Subject;

        entry.Level.Should().Be(LogLevel.Debug);
        entry.Exception.Should().BeNull();
    }

    private static async Task<Outcome> HandleAsync(
        Exception exception,
        CancellationToken requestAborted = default)
    {
        var log = new CapturingLogger();
        var handler = new CatalogExceptionHandler(log);

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
    /// Asserting only the response would let either fail unnoticed.
    /// </summary>
    private sealed class CapturingLogger : ILogger<CatalogExceptionHandler>
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

    private static DbUpdateException UniqueViolation(string constraintName) => new(
        "An error occurred while saving the entity changes.",
        // PostgresException exposes its error fields as read-only, so the constraint name
        // can only be supplied through the full constructor.
        new PostgresException(
            "duplicate key value violates unique constraint",
            "ERROR",
            "ERROR",
            PostgresErrorCodes.UniqueViolation,
            schemaName: "public",
            tableName: "products",
            constraintName: constraintName));

    private static DbUpdateException DataError(string sqlState) => new(
        "An error occurred while saving the entity changes.",
        new PostgresException(
            "value does not fit the column",
            "ERROR",
            "ERROR",
            sqlState));

    private sealed record Outcome(
        bool Handled,
        int Status,
        string? ContentType,
        string RawBody,
        ProblemPayload? Problem,
        CapturingLogger Log);

    /// <summary>
    /// Mirrors the wire shape so the correlationId extension can be asserted as a string
    /// rather than dug out of a JsonElement.
    /// </summary>
    private sealed record ProblemPayload
    {
        public int? Status { get; init; }

        public string? Title { get; init; }

        public string? Detail { get; init; }

        public string? Instance { get; init; }

        public string? CorrelationId { get; init; }
    }
}
