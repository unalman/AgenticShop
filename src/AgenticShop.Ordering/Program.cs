using AgenticShop.Ordering.Clients;
using AgenticShop.Ordering.Data;
using AgenticShop.Ordering.Endpoints;
using AgenticShop.Ordering.Errors;
using AgenticShop.Ordering.Health;
using AgenticShop.Ordering.Messaging;
using AgenticShop.Shared.Health;
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

// The one collaborator the "no service layer" rule was written to allow; see its remarks and
// docs/DECISIONS.md. Scoped, so it shares the request's DbContext and therefore its single
// SaveChangesAsync.
builder.Services.AddScoped<OrderPlacer>();

// The repository's first hosted service. Registered rather than started by hand so the host owns its
// lifetime and stops it on shutdown, which is what lets the publisher drain in-flight work instead of
// being killed mid-publish. It must never be allowed to throw out of ExecuteAsync: an unhandled
// exception in a hosted service stops the whole host, so a broker outage would take down an API that
// can still serve reads and still write outbox rows.
builder.Services.AddHostedService<OutboxPublisher>();

// The consumer that settles orders. Registered alongside the publisher because the flip is one change:
// from here on a placement writes a Pending order and publishes OrderPlaced, and this is what
// advances it. Registering one without the other would leave orders Pending forever, or settle them
// twice. Same rule as the publisher — nothing may escape ExecuteAsync, or a broker outage stops a
// host that could still be accepting and persisting orders.
builder.Services.AddHostedService<StockOutcomeConsumer>();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<OrderingExceptionHandler>();

// Two endpoints, deliberately, and the difference is the whole design.
//
// /health is liveness: the database this service owns and nothing else. A downstream outage must not
// make Ordering report itself down, or one failure becomes two — Stock goes down, an orchestrator
// pulls Ordering out of rotation, and Ordering stops serving the reads it could still have answered.
//
// /health/ready is readiness: the same database plus Catalog, because "can I accept an order right
// now" depends on resolving its lines against Catalog and nothing else.
//
// Stock is deliberately absent from readiness, and its absence is a consequence of the outbox rather
// than an oversight. Ordering makes no synchronous call to Stock any more, so a Stock outage does not
// stop Ordering accepting an order — it stops the order being *settled*, and the outbox buffers the
// event until Stock returns. Reporting unready because Stock is down would pull Ordering out of
// rotation and stop it accepting exactly the work the outbox exists to hold. That is the "one outage
// becomes two" failure the two-endpoint split exists to prevent, arriving through a different door.
//
// The broker is absent too, for now. With an outbox a broker outage is degraded rather than down —
// orders are accepted and persisted, events queue in the table — so which endpoint it belongs on, if
// either, is a real question rather than an obvious one. Recorded in docs/KNOWN-ISSUES.md.
//
// The probe client is built apart from the typed clients so it stays outside the resilience
// pipeline, whose 35-second total timeout would turn a *slow* Catalog into a dead Ordering.
using var healthProbeClient = new HttpClient { Timeout = DownstreamHealthCheck.Timeout };

builder.Services
    .AddHealthChecks()
    .AddDbContextCheck<OrderingDbContext>(name: HealthEndpoint.DatabaseCheckName)
    // The base URL is resolved by a lambda rather than read into a Uri here, for the reason
    // ARCHITECTURE.md §5.4 gives for the connection string: a test host's configuration override
    // does not exist yet, so a value read now freezes appsettings.Development.json and the probe
    // dials whatever is listening on the developer's own 5081. Startup still fails fast on a missing
    // or malformed key — RequireServiceBaseUrl runs again on app.Configuration below.
    .AddDownstreamCheck(
        "Catalog",
        () => new Uri(RequireServiceBaseUrl(builder.Configuration, "Catalog")),
        healthProbeClient);

builder.Services.AddOpenApi();

var app = builder.Build();

// Fail at startup rather than on the first request. Without this a missing or mistyped
// ConnectionStrings__Ordering yields a service that boots cleanly, reports healthy, and then
// returns 500 on every request that touches the database. Deliberately called after Build()
// so it validates the same merged configuration the DbContext will resolve.
RequireConnectionString(app.Configuration);

// Same reasoning, and it is not covered by the check above: a typed client with no base address
// fails on the first outbound call, which is the middle of a placement. Refusing to start is
// strictly better than compensating for a configuration typo. Catalog is the only downstream this
// service calls over HTTP now — Stock is reached through the broker, so it has no base URL to
// validate and none is required.
RequireServiceBaseUrl(app.Configuration, "Catalog");

// Same reasoning, and it is the publisher's turn to be covered: a missing or malformed broker URL
// would otherwise surface as a warning from a background loop every five seconds rather than as a
// startup failure naming the key that is wrong. Note this validates the *configuration*, not the
// connection — Ordering must still start when the broker is down, because the outbox exists precisely
// so that a placement can be written while the broker is unreachable.
OutboxPublisher.RequireBrokerUrl(app.Configuration);

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

app.MapHealthChecks(
    HealthEndpoint.Path,
    HealthEndpoint.Liveness(app.Environment.IsDevelopment()));

app.MapHealthChecks(
    HealthEndpoint.ReadyPath,
    HealthEndpoint.Ready(app.Environment.IsDevelopment()));

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
