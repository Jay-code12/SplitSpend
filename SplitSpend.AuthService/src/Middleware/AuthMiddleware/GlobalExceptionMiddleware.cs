using SplitSpend.AuthService.Application.DTOs;
using SplitSpend.AuthService.Settings;
using System.Diagnostics;
using System.Text.Json;

namespace SplitSpend.AuthService.Middleware.AuthMiddleware
{
    // ── Global Exception Handler ───────────────────────────────────────────────────
    public sealed class GlobalExceptionMiddleware(
        RequestDelegate next,
        ILogger<GlobalExceptionMiddleware> logger,
        IWebHostEnvironment env)
    {
        public async Task InvokeAsync(HttpContext context)
        {
            try { await next(context); }
            catch (Exception ex) { await HandleAsync(context, ex); }
        }

        private async Task HandleAsync(HttpContext context, Exception ex)
        {
            var correlationId = context.Items["X-Correlation-Id"]?.ToString() ?? "unknown";

            var activity = Activity.Current;
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.SetTag("exception.type", ex.GetType().Name);

            var (statusCode, error, message) = ex switch
            {
                AuthException ae => (ae.StatusCode, GetErrorCode(ae.StatusCode), ae.Message),
                OperationCanceledException => (408, "RequestTimeout", "Request timed out."),
                _ => (500, "InternalError", env.IsDevelopment()
                    ? ex.Message
                    : "An unexpected error occurred.")
            };

            if (statusCode >= 500)
                logger.LogError(ex,
                    "Unhandled exception. CorrelationId={CorrelationId} Path={Path}",
                    correlationId, context.Request.Path);
            else
                logger.LogWarning(
                    "Auth error. Status={Status} Message={Message} CorrelationId={CorrelationId}",
                    statusCode, message, correlationId);

            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json";

            var body = JsonSerializer.Serialize(
                new ServiceErrorResponse(correlationId, statusCode, error, message),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

            await context.Response.WriteAsync(body);
        }

        private static string GetErrorCode(int status) => status switch
        {
            400 => "BadRequest",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "NotFound",
            409 => "Conflict",
            423 => "AccountLocked",
            _ => "Error"
        };
    }
}
