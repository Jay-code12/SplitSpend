using System.Net;
using TransferService.Domain.Entities;

namespace TransferService.Middleware;

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
        try { await _next(ctx); }
        catch (Exception ex) { await HandleAsync(ctx, ex); }
    }

    private async Task HandleAsync(HttpContext ctx, Exception ex)
    {
        var traceId = ctx.TraceIdentifier;

        var (status, message) = ex switch
        {
            TransferNotFoundException       e => (HttpStatusCode.NotFound,              e.Message),
            TransferNotOwnedException       e => (HttpStatusCode.Forbidden,             e.Message),
            TransferDomainException         e => (HttpStatusCode.BadRequest,            e.Message),
            DuplicateIdempotencyKeyException e => (HttpStatusCode.Conflict,             e.Message),
            InvalidWebhookSignatureException e => (HttpStatusCode.BadRequest,           e.Message),
            PaystackApiException            e => (HttpStatusCode.UnprocessableEntity,   e.Message),
            ArgumentException               e => (HttpStatusCode.BadRequest,            e.Message),
            _                                => (HttpStatusCode.InternalServerError,    "An unexpected error occurred.")
        };

        if (status == HttpStatusCode.InternalServerError)
            _log.LogError(ex, "Unhandled exception [TraceId={TraceId}]", traceId);
        else
            _log.LogWarning(ex, "[{Status}] [TraceId={TraceId}]: {Message}",
                (int)status, traceId, message);

        ctx.Response.StatusCode  = (int)status;
        ctx.Response.ContentType = "application/json";

        await ctx.Response.WriteAsJsonAsync(new
        {
            error   = message,
            traceId,
            status  = (int)status
        });
    }
}
