using System.Diagnostics;

namespace SplitSpend.AuthService.Middleware.AuthMiddleware
{

    // ── Correlation ID ─────────────────────────────────────────────────────────────
    public sealed class CorrelationIdMiddleware(RequestDelegate next)
    {
        private const string Header = "X-Correlation-Id";

        public async Task InvokeAsync(HttpContext context)
        {
            var correlationId = context.Request.Headers[Header].FirstOrDefault()
                                ?? Guid.NewGuid().ToString("N");

            context.Items[Header] = correlationId;

            var activity = Activity.Current;
            activity?.SetTag("correlation.id", correlationId);
            activity?.SetBaggage("correlationId", correlationId);

            using (Serilog.Context.LogContext.PushProperty("CorrelationId", correlationId))
            using (Serilog.Context.LogContext.PushProperty("RequestPath", context.Request.Path.Value))
            {
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers[Header] = correlationId;
                    return Task.CompletedTask;
                });

                await next(context);
            }
        }
    }

}
