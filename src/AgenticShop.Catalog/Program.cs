using AgenticShop.Catalog.Data;
using AgenticShop.Catalog.Endpoints;
using AgenticShop.Catalog.Errors;
using AgenticShop.Catalog.Logging;
using AgenticShop.Shared.Middleware;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Context;

var builder = WebApplication.CreateBuilder(args);

// Levels and category overrides come from appsettings.json, where each one is justified; ambient
// enrichment and the access-log severity policy are in CatalogLogging.
builder.Host.UseSerilog((context, configuration) => configuration
    .AddCatalogLogging(context.Configuration)
    .WriteTo.Console(outputTemplate: CatalogLogging.ConsoleOutputTemplate));

// Resolved lazily, inside the options callback, so that configuration sources the host
// adds after Program.cs has run are visible — notably the override that
// WebApplicationFactory.ConfigureAppConfiguration applies in the integration tests.
// Capturing the value into a local here instead would freeze it before those overrides
// exist, and the tests would silently run against the developer's own database.
builder.Services.AddDbContext<CatalogDbContext>(options => options
    .UseNpgsql(RequireConnectionString(builder.Configuration))
    .UseSnakeCaseNamingConvention());

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<CatalogExceptionHandler>();
builder.Services.AddHealthChecks();
builder.Services.AddOpenApi();

var app = builder.Build();

// Fail at startup rather than on the first request. Without this a missing or mistyped
// ConnectionStrings__Catalog yields a service that boots cleanly, reports healthy, and
// then returns 500 on every request that touches the database. Deliberately called after
// Build() so it validates the same merged configuration the DbContext will resolve.
RequireConnectionString(app.Configuration);

// Correlation id first so failures raised by later middleware still carry it.
app.UseCorrelationId();

// Makes the resolved id ambient for every line written while handling the request, including EF
// Core's, which carries no correlation id of its own. Registered outside the request logger and
// the exception handler on purpose: a push placed after either would already be disposed by the
// time the request-completion event is written, and the id would be missing from it.
app.Use(async (context, next) =>
{
    using (LogContext.PushProperty(CatalogLogging.CorrelationIdProperty, context.GetCorrelationId()))
    {
        await next();
    }
});

// One structured line per completed request, which is what makes a validation rejection visible.
// The filter returns a result rather than throwing, so it never reaches the exception handler, and
// its 400 previously left no trace anywhere — "clients are sending invalid data" was invisible.
// A line per request rather than per rejected field is what keeps malformed traffic from becoming
// spam, and the status code and path arrive as properties rather than prose, so the volume is
// aggregatable instead of merely readable.
app.UseSerilogRequestLogging(options => options.GetLevel = CatalogLogging.RequestLogLevel);

app.UseExceptionHandler();

app.MapHealthChecks("/health");

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();

    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider
        .GetRequiredService<CatalogDbContext>()
        .Database
        .MigrateAsync();
}

app.MapProductEndpoints();

app.Run();

static string RequireConnectionString(IConfiguration configuration)
{
    var connectionString = configuration.GetConnectionString("Catalog");

    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException(
            "Connection string 'Catalog' is required. Set ConnectionStrings:Catalog in " +
            "configuration or ConnectionStrings__Catalog as an environment variable.");
    }

    return connectionString;
}

/// <summary>Anchor for WebApplicationFactory in the integration tests.</summary>
public partial class Program;
