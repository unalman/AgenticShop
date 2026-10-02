using AgenticShop.Catalog.Data;
using AgenticShop.Catalog.Endpoints;
using AgenticShop.Catalog.Errors;
using AgenticShop.Shared.Middleware;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

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
