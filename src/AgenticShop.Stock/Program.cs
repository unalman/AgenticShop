using AgenticShop.Shared.Logging;
using AgenticShop.Shared.Middleware;
using AgenticShop.Shared.Tracing;
using AgenticShop.Stock.Data;
using AgenticShop.Stock.Endpoints;
using AgenticShop.Stock.Errors;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Levels and category overrides come from appsettings.json, where each one is justified; ambient
// enrichment and the access-log severity policy are in ServiceLogging.
builder.Host.UseSerilog((context, configuration) => configuration
    .AddServiceLogging(context.Configuration)
    .WriteTo.Console(outputTemplate: ServiceLogging.ConsoleOutputTemplate));

// One span per inbound request, exported to the console here so the span tree is visible with no
// collector running, and over OTLP whenever OTEL_EXPORTER_OTLP_ENDPOINT is set. Stock is a leaf
// service: no outbound calls, so no HttpClient instrumentation.
builder.Services.AddServiceTracing(
    builder.Configuration,
    serviceName: "AgenticShop.Stock",
    exportToConsole: builder.Environment.IsDevelopment());

// Resolved lazily, inside the options callback, so that configuration sources the host
// adds after Program.cs has run are visible — notably the override that
// WebApplicationFactory.ConfigureAppConfiguration applies in the integration tests.
// Capturing the value into a local here instead would freeze it before those overrides
// exist, and the tests would silently run against the developer's own database.
builder.Services.AddDbContext<StockDbContext>(options => options
    .UseNpgsql(RequireConnectionString(builder.Configuration))
    .UseSnakeCaseNamingConvention());

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<StockExceptionHandler>();
builder.Services.AddHealthChecks();
builder.Services.AddOpenApi();

var app = builder.Build();

// Fail at startup rather than on the first request. Without this a missing or mistyped
// ConnectionStrings__Stock yields a service that boots cleanly, reports healthy, and then
// returns 500 on every request that touches the database. Deliberately called after Build()
// so it validates the same merged configuration the DbContext will resolve.
RequireConnectionString(app.Configuration);

// Correlation id first so failures raised by later middleware still carry it. The middleware also
// makes the id ambient for every log line of the request, which is why it has to be out here.
app.UseCorrelationId();

// One structured line per completed request, at Information whatever the status: severity belongs to
// the exception handler alone. This is what makes a validation rejection visible, because the filter
// returns a result rather than throwing and so never reaches the handler.
app.UseSerilogRequestLogging(options => options.GetLevel = ServiceLogging.RequestLogLevel);

app.UseExceptionHandler();

app.MapHealthChecks("/health");

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();

    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider
        .GetRequiredService<StockDbContext>()
        .Database
        .MigrateAsync();
}

app.MapStockEndpoints();
app.MapReservationEndpoints();

app.Run();

static string RequireConnectionString(IConfiguration configuration)
{
    var connectionString = configuration.GetConnectionString("Stock");

    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException(
            "Connection string 'Stock' is required. Set ConnectionStrings:Stock in " +
            "configuration or ConnectionStrings__Stock as an environment variable.");
    }

    return connectionString;
}

/// <summary>Anchor for WebApplicationFactory in the integration tests.</summary>
public partial class Program;
