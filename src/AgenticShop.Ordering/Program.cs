using AgenticShop.Ordering.Clients;
using AgenticShop.Ordering.Data;
using AgenticShop.Ordering.Endpoints;
using AgenticShop.Ordering.Errors;
using AgenticShop.Shared.Logging;
using AgenticShop.Shared.Middleware;
using AgenticShop.Shared.Tracing;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Levels and category overrides come from appsettings.json, where each one is justified; ambient
// enrichment and the access-log severity policy are in ServiceLogging.
builder.Host.UseSerilog((context, configuration) => configuration
    .AddServiceLogging(context.Configuration)
    .WriteTo.Console(outputTemplate: ServiceLogging.ConsoleOutputTemplate));

// One span per inbound request plus one per outbound call, which is what makes the Ordering ->
// Catalog / Stock hop visible as a tree rather than as two unrelated log lines. Exported to the
// console here so that is observable with no collector running, and over OTLP whenever
// OTEL_EXPORTER_OTLP_ENDPOINT is set.
builder.Services.AddServiceTracing(
    builder.Configuration,
    serviceName: "AgenticShop.Ordering",
    exportToConsole: builder.Environment.IsDevelopment(),
    instrumentHttpClient: true);

// Resolved lazily, inside the options callback, so that configuration sources the host
// adds after Program.cs has run are visible — notably the override that
// WebApplicationFactory.ConfigureAppConfiguration applies in the integration tests.
// Capturing the value into a local here instead would freeze it before those overrides
// exist, and the tests would silently run against the developer's own database.
builder.Services.AddDbContext<OrderingDbContext>(options => options
    .UseNpgsql(RequireConnectionString(builder.Configuration))
    .UseSnakeCaseNamingConvention());

// No DelegatingHandler is registered on these clients to carry a correlation id, and none is
// needed: traceparent propagation is done by the HttpClient instrumentation added above, which
// injects it on every attempt including retries. The handler that used to do this by hand —
// CorrelationIdPropagatingHandler, plus the AddHttpContextAccessor it required — is gone, because
// the inbound X-Correlation-Id it forwarded is no longer adopted by anything. See docs/DECISIONS.md.
//
// The downstream timeout O15 documented now lives in the resilience pipeline as a per-attempt
// timeout, so HttpClient.Timeout is deliberately not set here. Setting it would be a bug rather
// than a redundant safety net: HttpClient.Timeout bounds the whole handler pipeline, retries
// included, so a ten-second value would abort the sequence before a second attempt could start.
// Left unset it defaults to 100 seconds, which sits above the pipeline's 35-second total timeout
// and therefore never fires first.
builder.Services
    .AddHttpClient<ICatalogClient, CatalogClient>(client =>
        client.BaseAddress = new Uri(RequireServiceBaseUrl(builder.Configuration, "Catalog")))
    .AddDownstreamResilience();

builder.Services
    .AddHttpClient<IStockClient, StockClient>(client =>
        client.BaseAddress = new Uri(RequireServiceBaseUrl(builder.Configuration, "Stock")))
    .AddDownstreamResilience();

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

// Correlation id first so failures raised by later middleware still carry it. The middleware also
// makes the id ambient for every log line of the request, which is why it has to be out here — and
// out here it also wraps the resilience pipeline, so a retry is logged against the request that
// caused it.
app.UseCorrelationId();

// One structured line per completed request, at Information whatever the status: severity belongs to
// the exception handler alone. Verified against a real dependency failure — with the library default
// a 502 produced an Error here *and* in the handler, plus another from the resilience pipeline's
// final attempt, so one classified failure filled the log with three Errors saying the same thing.
app.UseSerilogRequestLogging(options => options.GetLevel = ServiceLogging.RequestLogLevel);

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
