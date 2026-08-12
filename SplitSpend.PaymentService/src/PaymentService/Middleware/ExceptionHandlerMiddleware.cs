using System.Net;
using PaymentService.Domain.Entities;

namespace PaymentService.Middleware;

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
            VirtualAccountNotFoundException e => (HttpStatusCode.NotFound,           e.Message),
            UserResolutionException         e => (HttpStatusCode.UnprocessableEntity, e.Message),
            InvalidWebhookSignatureException e => (HttpStatusCode.BadRequest,         e.Message),
            DuplicatePaymentException       e => (HttpStatusCode.Conflict,            e.Message),
            PaymentDomainException          e => (HttpStatusCode.BadRequest,          e.Message),
            ArgumentException               e => (HttpStatusCode.BadRequest,          e.Message),
            _                                => (HttpStatusCode.InternalServerError,  "An unexpected error occurred.")
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
