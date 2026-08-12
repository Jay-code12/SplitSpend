using System.Net;
using WalletService.Domain.Entities;

namespace WalletService.Middleware;

/// <summary>
/// Catches unhandled exceptions and returns a consistent JSON error response.
/// Logs the full stack trace using structured logging with TraceId for correlation.
/// </summary>
public class ExceptionHandlerMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlerMiddleware> _log;

    public ExceptionHandlerMiddleware(RequestDelegate next, ILogger<ExceptionHandlerMiddleware> log)
    {
        _next = next;
        _log  = log;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        try
        {
            await _next(ctx);
        }
        catch (Exception ex)
        {
            await HandleAsync(ctx, ex);
        }
    }

    private async Task HandleAsync(HttpContext ctx, Exception ex)
    {
        var traceId = ctx.TraceIdentifier;

        var (status, message) = ex switch
        {
            WalletNotFoundException       e => (HttpStatusCode.NotFound,              e.Message),
            InsufficientFundsException    e => (HttpStatusCode.UnprocessableEntity,   e.Message),
            WalletSuspendedException      e => (HttpStatusCode.UnprocessableEntity,   e.Message),
            DuplicateIdempotencyKeyException e => (HttpStatusCode.Conflict,           e.Message),
            ArgumentException             e => (HttpStatusCode.BadRequest,            e.Message),
            _                              => (HttpStatusCode.InternalServerError,    "An unexpected error occurred.")
        };

        if (status == HttpStatusCode.InternalServerError)
            _log.LogError(ex, "Unhandled exception [TraceId={TraceId}]", traceId);
        else
            _log.LogWarning(ex, "Handled exception [{Status}] [TraceId={TraceId}]: {Message}",
                (int)status, traceId, message);

        ctx.Response.StatusCode  = (int)status;
        ctx.Response.ContentType = "application/json";

        await ctx.Response.WriteAsJsonAsync(new
        {
            error   = message,
            traceId = traceId,
            status  = (int)status
        });
    }
}
