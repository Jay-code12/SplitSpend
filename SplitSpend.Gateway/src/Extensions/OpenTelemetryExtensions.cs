using Azure.Monitor.OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using SplitSpend.Gateway.Configuration;

namespace SplitSpend.Gateway.Extensions;

public static class OpenTelemetryExtensions
{
    /// <summary>
    /// Registers OpenTelemetry tracing for the gateway.
    ///
    /// What is instrumented:
    ///   • Every inbound ASP.NET Core request  → creates a root span
    ///   • Every outbound HttpClient call       → creates a child span per downstream service
    ///   • Ocelot's internal HttpClient is also captured because UseTracing = true in ocelot.json
    ///
    /// Exporters:
    ///   • OTLP   → Jaeger / Grafana Tempo / any OTLP-compatible collector (local dev)
    ///   • Azure Monitor → Application Insights (production)
    ///
    /// Correlation propagation:
    ///   W3C TraceContext headers are injected into every outbound HTTP call automatically
    ///   by the OpenTelemetry.Instrumentation.Http package. Every downstream service that
    ///   also uses OpenTelemetry will continue the same trace — no manual header copying needed.
    ///
    ///   The CorrelationId the gateway generates in CorrelationIdMiddleware is stamped as
    ///   a span tag and as baggage so it is visible alongside the OTel TraceId in every
    ///   downstream service's spans and logs.
    /// </summary>
    public static IServiceCollection AddSplitSpendOpenTelemetry(
        this IServiceCollection services,
        OpenTelemetrySettings   settings)
    {
        services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(
                    serviceName:    settings.ServiceName,
                    serviceVersion: settings.ServiceVersion)
                .AddAttributes(new Dictionary<string, object>
                {
                    ["deployment.environment"] = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production",
                    ["service.component"]      = "api-gateway"
                }))
            .WithTracing(tracing =>
            {
                tracing
                    // ── Sources ───────────────────────────────────────────
                    .AddAspNetCoreInstrumentation(opts =>
                    {
                        opts.RecordException = true;

                        // Enrich every inbound span with HTTP request details
                        opts.EnrichWithHttpRequest = (activity, request) =>
                        {
                            activity.SetTag("gateway.request.host",   request.Host.Value);
                            activity.SetTag("gateway.request.scheme", request.Scheme);

                            // Pull the CorrelationId the middleware already set
                            if (request.Headers.TryGetValue("X-Correlation-Id", out var cid))
                                activity.SetTag("correlation.id", cid.ToString());
                        };

                        // Enrich every inbound span with HTTP response details
                        opts.EnrichWithHttpResponse = (activity, response) =>
                        {
                            activity.SetTag("gateway.response.status", response.StatusCode);
                        };

                        // Filter out health check noise from traces
                        opts.Filter = ctx =>
                            !ctx.Request.Path.StartsWithSegments("/health");
                    })

                    .AddHttpClientInstrumentation(opts =>
                    {
                        opts.RecordException = true;

                        // Tag which downstream service is being called based on the URL
                        opts.EnrichWithHttpRequestMessage = (activity, request) =>
                        {
                            if (request.RequestUri is not null)
                            {
                                activity.SetTag("downstream.url",  request.RequestUri.ToString());
                                activity.SetTag("downstream.host", request.RequestUri.Host);
                            }
                        };

                        opts.EnrichWithHttpResponseMessage = (activity, response) =>
                        {
                            activity.SetTag("downstream.status", (int)response.StatusCode);
                        };
                    })

                    // ── Exporters ─────────────────────────────────────────
                    .AddOtlpExporter(opts =>
                    {
                        opts.Endpoint = new Uri(settings.OtlpEndpoint);
                    });

                // Add Azure Monitor only when the connection string is configured
                if (!string.IsNullOrWhiteSpace(settings.AzureMonitorConnectionString))
                {
                    tracing.AddAzureMonitorTraceExporter(opts =>
                    {
                        opts.ConnectionString = settings.AzureMonitorConnectionString;
                    });
                }
            });

        return services;
    }
}
