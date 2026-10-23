using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace AgenticShop.Ordering.Clients;

/// <summary>
/// The resilience pipeline Ordering's typed clients run through.
/// </summary>
/// <remarks>
/// <para>
/// Retry is safe for every outbound call this service makes, which since Phase 2 means one kind of
/// call: reading a product from Catalog. A read has no side effect, so retrying it after a lost
/// response costs a round trip and nothing more.
/// </para>
/// <para>
/// <b>There used to be a per-request exemption here, and its reasoning still binds.</b> The
/// synchronous reserve call opted itself out of retry, because Stock answered 409 for a repeated
/// <c>(order_id, stock_item_id)</c> through its unique index and the client read 409 as "this line
/// cannot be held": if a first attempt committed and only its 201 was lost, the retry would turn a
/// hold that exists into a reported refusal — the order fails as out of stock and the first hold is
/// stranded. That call is gone, because stock is now held by publishing <c>OrderPlaced</c> and the
/// outbox makes the emission idempotent at the source rather than at the downstream. So there is
/// nothing left to exempt, and the marker that expressed it is gone with it. <b>Any future outbound
/// write must re-earn that exemption rather than inherit this pipeline's retry.</b>
/// <c>docs/DECISIONS.md</c> → O18.
/// </para>
/// </remarks>
public static class DownstreamResilience
{
    /// <summary>The pipeline name, used in metrics and logs.</summary>
    public const string PipelineName = "downstream";

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
    /// Adds the timeout, retry and circuit-breaker strategies to a typed client.
    /// </summary>
    /// <remarks>
    /// This used to have to be called after a <c>CorrelationIdPropagatingHandler</c>, because
    /// handlers run outermost-first and the id had to be on the message before a retry re-sent it.
    /// That handler is gone — trace context is propagated by the HttpClient instrumentation instead —
    /// so the registration order no longer carries a correctness constraint.
    /// </remarks>
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

    /// <summary>
    /// Retry on the library's own definition of "transient" — 408, 429 and 5xx plus
    /// <see cref="HttpRequestException"/> and <see cref="TimeoutRejectedException"/> — with no
    /// predicate of ours wrapped around it. Nothing this service sends needs exempting any more; a
    /// normal 4xx is never retried because the library already says so, and the definition cannot
    /// drift from the library's if it is ever revised.
    /// </summary>
    private static HttpRetryStrategyOptions BuildRetryOptions() => new()
    {
        MaxRetryAttempts = MaxRetryAttempts,
        Delay = RetryDelay,
        BackoffType = DelayBackoffType.Constant
    };
}
