using System.Diagnostics;
using SplitSpend.Gateway.Models;

namespace SplitSpend.Gateway.Middleware;

/// <summary>
/// Generates or adopts an X-Correlation-Id on every inbound request and propagates
/// it downstream on every outgoing call. Also enriches Serilog's LogContext and
/// the current Activity (OpenTelemetry span) with the correlation data so every
/// log line and trace span carries the same identifiers.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    private const string CorrelationHeader = GatewayHeaders.CorrelationId;
    private const string TraceHeader       = GatewayHeaders.TraceId;

    public async Task InvokeAsync(HttpContext context)
    {
        // ── 1. Resolve or generate Correlation ID ─────────────────────────
        var correlationId = context.Request.Headers[CorrelationHeader].FirstOrDefault()
                            ?? Guid.NewGuid().ToString("N");

        // ── 2. Bind to the current Activity (OpenTelemetry span) ──────────
        var activity = Activity.Current;
        var traceId  = activity?.TraceId.ToString() ?? correlationId;

        if (activity is not null)
        {
            activity.SetTag("correlation.id",  correlationId);
            activity.SetTag("http.route",       context.Request.Path);
            activity.SetBaggage("correlationId", correlationId);
        }

        // ── 3. Store in HttpContext.Items for downstream middleware/controllers
        context.Items[CorrelationHeader] = correlationId;
        context.Items[TraceHeader]       = traceId;

        // ── 4. Enrich Serilog log context for this entire request scope ───
        using (Serilog.Context.LogContext.PushProperty("CorrelationId",  correlationId))
        using (Serilog.Context.LogContext.PushProperty("TraceId",        traceId))
        using (Serilog.Context.LogContext.PushProperty("RequestPath",    context.Request.Path.Value))
        using (Serilog.Context.LogContext.PushProperty("RequestMethod",  context.Request.Method))
        {
            // ── 5. Stamp the response headers ─────────────────────────────
            context.Response.OnStarting(() =>
            {
                context.Response.Headers[CorrelationHeader]       = correlationId;
                context.Response.Headers[TraceHeader]             = traceId;
                context.Response.Headers[GatewayHeaders.GatewayVersion] = "1.0.0";
                return Task.CompletedTask;
            });

            logger.LogInformation(
                "Gateway request received. CorrelationId={CorrelationId} TraceId={TraceId} Method={Method} Path={Path}",
                correlationId, traceId, context.Request.Method, context.Request.Path);

            await next(context);

            logger.LogInformation(
                "Gateway request completed. CorrelationId={CorrelationId} StatusCode={StatusCode} ElapsedMs={ElapsedMs}",
                correlationId, context.Response.StatusCode,
                (DateTime.UtcNow - DateTime.UtcNow).TotalMilliseconds); // replaced by timing middleware
        }
    }
}
