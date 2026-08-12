using System.Diagnostics;
using System.Text.Json;
using SplitSpend.UserService.Application.DTOs;
using SplitSpend.UserService.Common;

namespace SplitSpend.UserService.Middleware;

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
        using (Serilog.Context.LogContext.PushProperty("RequestPath",   context.Request.Path.Value))
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

        Activity.Current?.SetStatus(ActivityStatusCode.Error, ex.Message);
        Activity.Current?.SetTag("exception.type", ex.GetType().Name);

        var (statusCode, error, message) = ex switch
        {
            UserException ue           => (ue.StatusCode, GetErrorCode(ue.StatusCode), ue.Message),
            OperationCanceledException => (408, "RequestTimeout", "The request timed out."),
            _                          => (500, "InternalError", env.IsDevelopment()
                                            ? ex.Message
                                            : "An unexpected error occurred.")
        };

        if (statusCode >= 500)
            logger.LogError(ex,
                "Unhandled exception. CorrelationId={CorrelationId} Path={Path}",
                correlationId, context.Request.Path);
        else
            logger.LogWarning(
                "User service error. Status={Status} Message={Message} CorrelationId={CorrelationId}",
                statusCode, message, correlationId);

        context.Response.StatusCode  = statusCode;
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
        _   => "Error"
    };
}
