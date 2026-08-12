using System.Net;
using System.Text.Json;
using SplitSpend.Gateway.Models;

namespace SplitSpend.Gateway.Middleware;

/// <summary>
/// Last-resort exception handler. Catches any unhandled exception thrown by
/// downstream middleware or Ocelot, logs it with full context, and returns
/// a structured JSON error envelope so clients always receive a consistent shape.
/// Never leaks stack traces to clients in non-development environments.
/// </summary>
public sealed class GlobalExceptionMiddleware(
    RequestDelegate next,
    ILogger<GlobalExceptionMiddleware> logger,
    IWebHostEnvironment env)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception ex)
    {
        var correlationId = context.Items[GatewayHeaders.CorrelationId]?.ToString()
                            ?? "unknown";
        var traceId       = context.Items[GatewayHeaders.TraceId]?.ToString()
                            ?? correlationId;
        var userId        = context.Items["UserId"]?.ToString() ?? "anonymous";

        // Tag the OTel span as errored
        var activity = System.Diagnostics.Activity.Current;
        activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
        activity?.SetTag("exception.type",    ex.GetType().Name);
        activity?.SetTag("exception.message", ex.Message);

        logger.LogError(ex,
            "Unhandled gateway exception. CorrelationId={CorrelationId} UserId={UserId} Path={Path}",
            correlationId, userId, context.Request.Path);

        var (statusCode, errorCode, message) = ClassifyException(ex);

        context.Response.StatusCode  = statusCode;
        context.Response.ContentType = "application/json";

        var response = new GatewayErrorResponse
        {
            TraceId  = traceId,
            Status   = statusCode,
            Error    = errorCode,
            Message  = env.IsDevelopment() ? ex.Message : message
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(response,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    private static (int statusCode, string errorCode, string message) ClassifyException(Exception ex) =>
        ex switch
        {
            OperationCanceledException  => (StatusCodes.Status408RequestTimeout,
                                            "RequestTimeout",
                                            "The request timed out."),
            HttpRequestException hre    => ((int)(hre.StatusCode ?? HttpStatusCode.BadGateway),
                                            "BadGateway",
                                            "A downstream service returned an error."),
            UnauthorizedAccessException => (StatusCodes.Status403Forbidden,
                                            "Forbidden",
                                            "You do not have permission to perform this action."),
            _                           => (StatusCodes.Status500InternalServerError,
                                            "InternalError",
                                            "An unexpected error occurred. Please try again later.")
        };
}
