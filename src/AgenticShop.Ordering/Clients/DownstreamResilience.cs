using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace AgenticShop.Ordering.Clients;

/// <summary>
/// The resilience pipeline both typed clients run through, and the marker that exempts one request
/// from retry.
/// </summary>
/// <remarks>
/// <para>
/// Retry is safe for every outbound call this service makes <b>except</b> creating a reservation, so
/// the exemption is per request rather than per client or per HTTP method. The library's own
/// <c>DisableForUnsafeHttpMethods</c> would have been the one-liner, but it disables retry for all
/// POSTs — and confirm and release are POSTs that are safe to retry, because a 409 from either is
/// already resolved by the reconciliation read (decisions O9 and O10) rather than guessed at.
/// </para>
/// <para>
/// Reserve is the exception because Stock answers 409 for a repeated <c>(order_id, stock_item_id)</c>
/// through its unique index, and <see cref="StockClient.ReserveAsync"/> reads 409 as "this line
/// cannot be held". If a first attempt committed and only its 201 was lost, a retry would therefore
/// turn a hold that exists into a reported refusal: the order fails as out-of-stock, and because the
/// refusal path deliberately skips the reconciliation read, the first hold is stranded.
/// <c>docs/DECISIONS.md</c> → O18.
/// </para>
/// </remarks>
public static class DownstreamResilience
{
    /// <summary>The pipeline name, used in metrics and logs.</summary>
    public const string PipelineName = "downstream";

    /// <summary>
    /// Set on a request that must not be retried. Read by the retry predicate below.
    /// </summary>
    public static readonly HttpRequestOptionsKey<bool> NotRetryable =
        new("AgenticShop.Ordering.NotRetryable");

    /// <summary>
    /// Bounds a single attempt. This is the ten seconds O15 introduced, unchanged in value — it is
    /// what makes "the dependency timed out" reachable at all — but it is now a policy rather than
    /// <c>HttpClient.Timeout</c>.
    /// </summary>
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Bounds the whole call including retries, so a placement cannot hold stock indefinitely.
    /// Sized for the worst case: three attempts at <see cref="AttemptTimeout"/> plus the delays
    /// between them.
    /// </summary>
    public static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(35);

    /// <summary>
    /// Constant rather than exponential. The caller is waiting synchronously on an order placement
    /// and stock is held for the duration, so a growing backoff buys nothing here.
    /// </summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>Two retries, so three attempts in total.</summary>
    public const int MaxRetryAttempts = 2;

    /// <summary>
    /// Samples required before the breaker can open. Polly's default of 100 would mean the breaker
    /// never engages at this project's traffic, and a circuit breaker that cannot trip is not one.
    /// </summary>
    public const int CircuitBreakerMinimumThroughput = 10;

    /// <summary>Half of a sampled window failing opens the circuit.</summary>
    public const double CircuitBreakerFailureRatio = 0.5;

    public static readonly TimeSpan CircuitBreakerSamplingDuration = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan CircuitBreakerBreakDuration = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Adds the timeout, retry and circuit-breaker strategies to a typed client. Call it
    /// <b>after</b> <c>AddHttpMessageHandler&lt;CorrelationIdPropagatingHandler&gt;</c>: handlers run
    /// outermost-first, so the correlation id is on the message before any retry re-sends it.
    /// </summary>
    public static IHttpClientBuilder AddDownstreamResilience(this IHttpClientBuilder builder)
    {
        builder.AddResilienceHandler(PipelineName, static pipeline =>
        {
            // Outermost to innermost — the order the library documents for its own standard
            // pipeline, minus the bulkhead, which nothing here asked for.
            pipeline.AddTimeout(new HttpTimeoutStrategyOptions { Timeout = TotalTimeout });

            pipeline.AddRetry(BuildRetryOptions());

            pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                MinimumThroughput = CircuitBreakerMinimumThroughput,
                FailureRatio = CircuitBreakerFailureRatio,
                SamplingDuration = CircuitBreakerSamplingDuration,
                BreakDuration = CircuitBreakerBreakDuration
            });

            pipeline.AddTimeout(new HttpTimeoutStrategyOptions { Timeout = AttemptTimeout });
        });

        return builder;
    }

    private static HttpRetryStrategyOptions BuildRetryOptions()
    {
        var options = new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = MaxRetryAttempts,
            Delay = RetryDelay,
            BackoffType = DelayBackoffType.Constant
        };

        // Wraps the library's predicate instead of restating it. That predicate is what decides
        // "transient" — 408, 429 and 5xx plus HttpRequestException and TimeoutRejectedException —
        // so a normal 4xx such as Stock's 409 is never retried, and the definition cannot drift from
        // the library's if it is ever revised.
        var isTransient = options.ShouldHandle;

        options.ShouldHandle = async arguments =>
            await isTransient(arguments).ConfigureAwait(false) && IsRetryEligible(arguments.Context);

        return options;
    }

    /// <summary>
    /// Fails closed. Without a request message there is no way to tell a retryable read from a
    /// reserve, and guessing wrong is the stranded-hold case above — so an unidentifiable request is
    /// not retried.
    /// </summary>
    private static bool IsRetryEligible(ResilienceContext context)
    {
        var request = context.GetRequestMessage();

        if (request is null)
        {
            return false;
        }

        return !request.Options.TryGetValue(NotRetryable, out var notRetryable) || !notRetryable;
    }
}
