using System.Net;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using SplitSpend.Gateway.Configuration;

namespace SplitSpend.Gateway.Extensions;

public static class HttpClientExtensions
{
    /// <summary>
    /// Registers the "aggregator" named HttpClient used by DashboardAggregator
    /// and VendorPayDetailAggregator. Applies a Polly resilience pipeline:
    ///
    ///   Retry          — 3 attempts with 200 ms exponential back-off, on transient HTTP errors
    ///   Circuit Breaker — opens after 5 failures in 30 s; breaks for 15 s
    ///   Timeout         — 30 s per individual attempt
    ///
    /// The OpenTelemetry HttpClient instrumentation automatically traces every
    /// request made through this client — no additional code needed.
    /// </summary>
    public static IServiceCollection AddSplitSpendHttpClients(
        this IServiceCollection services,
        ResilienceSettings      settings)
    {
        services
            .AddHttpClient("aggregator")
            .AddResilienceHandler("aggregator-pipeline", builder =>
            {
                // ── Timeout (innermost — applies per attempt) ──────────────
                builder.AddTimeout(TimeSpan.FromSeconds(settings.TimeoutSeconds));

                // ── Retry ─────────────────────────────────────────────────
                builder.AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts  = settings.Retry.MaxAttempts,
                    Delay             = TimeSpan.FromMilliseconds(settings.Retry.DelayMs),
                    BackoffType       = DelayBackoffType.Exponential,
                    UseJitter         = true,
                    ShouldHandle      = args => ValueTask.FromResult(
                        IsTransientHttpError(args.Outcome))
                });

                // ── Circuit Breaker ───────────────────────────────────────
                builder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
                {
                    FailureRatio              = (double)settings.CircuitBreaker.FailureThreshold / 10,
                    SamplingDuration          = TimeSpan.FromSeconds(settings.CircuitBreaker.SamplingDurationSeconds),
                    MinimumThroughput         = settings.CircuitBreaker.MinimumThroughput,
                    BreakDuration             = TimeSpan.FromSeconds(settings.CircuitBreaker.BreakDurationSeconds),
                    ShouldHandle              = args => ValueTask.FromResult(
                        IsTransientHttpError(args.Outcome))
                });
            });

        return services;
    }

    private static bool IsTransientHttpError(Outcome<HttpResponseMessage> outcome)
    {
        if (outcome.Exception is HttpRequestException or OperationCanceledException)
            return true;

        if (outcome.Result is { } response)
        {
            return response.StatusCode is
                HttpStatusCode.RequestTimeout       or
                HttpStatusCode.TooManyRequests      or
                HttpStatusCode.InternalServerError  or
                HttpStatusCode.BadGateway           or
                HttpStatusCode.ServiceUnavailable   or
                HttpStatusCode.GatewayTimeout;
        }

        return false;
    }
}
