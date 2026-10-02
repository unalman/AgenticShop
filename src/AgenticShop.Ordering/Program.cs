using AgenticShop.Ordering.Clients;
using AgenticShop.Ordering.Data;
using AgenticShop.Ordering.Endpoints;
using AgenticShop.Ordering.Errors;
using AgenticShop.Shared.Middleware;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Bounds a downstream call. This is not a resilience policy — there is no retry, and Polly is
// deliberately Phase 1 — it is the difference between a 502 after ten seconds and a request that
// hangs for HttpClient's 100-second default while stock stays held. Without a bound the
// confirm-phase policy would be unreachable in practice, because "timed out" would never arrive.
const int DownstreamTimeoutSeconds = 10;

// Resolved lazily, inside the options callback, so that configuration sources the host
// adds after Program.cs has run are visible — notably the override that
// WebApplicationFactory.ConfigureAppConfiguration applies in the integration tests.
// Capturing the value into a local here instead would freeze it before those overrides
// exist, and the tests would silently run against the developer's own database.
builder.Services.AddDbContext<OrderingDbContext>(options => options
    .UseNpgsql(RequireConnectionString(builder.Configuration))
    .UseSnakeCaseNamingConvention());

// Read by CorrelationIdPropagatingHandler so no client method has to carry a tracing concern
// in its signature.
builder.Services.AddHttpContextAccessor();
builder.Services.AddTransient<CorrelationIdPropagatingHandler>();

builder.Services
    .AddHttpClient<ICatalogClient, CatalogClient>(client =>
    {
        client.BaseAddress = new Uri(RequireServiceBaseUrl(builder.Configuration, "Catalog"));
        client.Timeout = TimeSpan.FromSeconds(DownstreamTimeoutSeconds);
    })
    .AddHttpMessageHandler<CorrelationIdPropagatingHandler>();

builder.Services
    .AddHttpClient<IStockClient, StockClient>(client =>
    {
        client.BaseAddress = new Uri(RequireServiceBaseUrl(builder.Configuration, "Stock"));
        client.Timeout = TimeSpan.FromSeconds(DownstreamTimeoutSeconds);
    })
    .AddHttpMessageHandler<CorrelationIdPropagatingHandler>();

// The one collaborator the "no service layer" rule was written to allow; see its remarks and
// docs/DECISIONS.md. Scoped, so it shares the request's DbContext and therefore its single
// SaveChangesAsync.
builder.Services.AddScoped<OrderPlacer>();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<OrderingExceptionHandler>();
builder.Services.AddHealthChecks();
builder.Services.AddOpenApi();

var app = builder.Build();

// Fail at startup rather than on the first request. Without this a missing or mistyped
// ConnectionStrings__Ordering yields a service that boots cleanly, reports healthy, and then
// returns 500 on every request that touches the database. Deliberately called after Build()
// so it validates the same merged configuration the DbContext will resolve.
RequireConnectionString(app.Configuration);

// Same reasoning, and it is not covered by the check above: a typed client with no base address
// fails on the first outbound call, which is the middle of a placement, after stock has been
// reserved. Refusing to start is strictly better than compensating for a configuration typo.
RequireServiceBaseUrl(app.Configuration, "Catalog");
RequireServiceBaseUrl(app.Configuration, "Stock");

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
        .GetRequiredService<OrderingDbContext>()
        .Database
        .MigrateAsync();
}

app.MapOrderEndpoints();

app.Run();

static string RequireConnectionString(IConfiguration configuration)
{
    var connectionString = configuration.GetConnectionString("Ordering");

    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException(
            "Connection string 'Ordering' is required. Set ConnectionStrings:Ordering in " +
            "configuration or ConnectionStrings__Ordering as an environment variable.");
    }

    return connectionString;
}

static string RequireServiceBaseUrl(IConfiguration configuration, string service)
{
    var key = $"Services:{service}:BaseUrl";
    var value = configuration[key];

    // Validated here rather than left to `new Uri(...)` at the first call, so a typo is reported with
    // the key that is wrong instead of as a UriFormatException mid-placement — which would be after
    // stock had been reserved.
    //
    // The scheme check is not redundant. Uri.TryCreate(..., UriKind.Absolute) accepts "localhost:5082"
    // because it reads "localhost" as the scheme, so an absolute-URI test alone would pass a value
    // that cannot reach anything.
    if (string.IsNullOrWhiteSpace(value)
        || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
        || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
    {
        throw new InvalidOperationException(
            $"Service base URL '{key}' is required and must be an absolute http or https URL. " +
            $"Set {key} in configuration or {key.Replace(':', '_')} as an environment variable.");
    }

    return value;
}

/// <summary>Anchor for WebApplicationFactory in the integration tests.</summary>
public partial class Program;
