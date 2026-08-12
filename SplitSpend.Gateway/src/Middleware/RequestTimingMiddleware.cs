using System.Diagnostics;
using SplitSpend.Gateway.Models;

namespace SplitSpend.Gateway.Middleware;

/// <summary>
/// Measures end-to-end request duration through the gateway pipeline.
/// Records elapsed milliseconds on the OTel span and in Serilog so Application
/// Insights can alert on slow routes without additional instrumentation.
/// </summary>
public sealed class RequestTimingMiddleware(
    RequestDelegate next,
    ILogger<RequestTimingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            await next(context);
        }
        finally
        {
            sw.Stop();
            var elapsed       = sw.ElapsedMilliseconds;
            var correlationId = context.Items[GatewayHeaders.CorrelationId]?.ToString() ?? "unknown";
            var statusCode    = context.Response.StatusCode;

            // Add elapsed to the OTel span
            var activity = Activity.Current;
            activity?.SetTag("gateway.duration_ms", elapsed);
            activity?.SetTag("http.status_code",    statusCode);

            var level = elapsed > 2000
                ? LogLevel.Warning   // slow route
                : LogLevel.Information;

            logger.Log(level,
                "Request completed. CorrelationId={CorrelationId} Method={Method} Path={Path} " +
                "StatusCode={StatusCode} ElapsedMs={ElapsedMs}",
                correlationId,
                context.Request.Method,
                context.Request.Path,
                statusCode,
                elapsed);
        }
    }
}
