using Serilog;
using Serilog.Events;
using SplitSpend.Gateway.Configuration;

namespace SplitSpend.Gateway.Extensions;

public static class SerilogExtensions
{
    /// <summary>
    /// Bootstraps Serilog with two sinks:
    ///   1. Rolling file — daily, retained 30 days, JSON format for audit
    ///   2. Seq           — centralised structured log server
    ///
    /// Every log entry is enriched with:
    ///   ServiceName, Environment, MachineName, ThreadId, CorrelationId (from LogContext),
    ///   and the OTel TraceId / SpanId (via the Serilog.Enrichers.Context enricher).
    ///
    /// The CorrelationId is pushed into Serilog's LogContext by CorrelationIdMiddleware
    /// so it appears on every log line emitted during that request, without needing
    /// to pass it explicitly to every logger call.
    /// </summary>
    public static WebApplicationBuilder AddSplitSpendSerilog(
        this WebApplicationBuilder builder,
        SeqSettings seqSettings)
    {
        builder.Host.UseSerilog((context, services, config) =>
        {
            config
                .ReadFrom.Configuration(context.Configuration)
                .ReadFrom.Services(services)

                // ── Enrichers ─────────────────────────────────────────────
                .Enrich.FromLogContext()                         // picks up CorrelationId, UserId, etc.
                .Enrich.WithMachineName()
                .Enrich.WithEnvironmentName()
                .Enrich.WithThreadId()
                .Enrich.WithProperty("ServiceName",  "SplitSpend.Gateway")
                .Enrich.WithProperty("ServiceVersion", "1.0.0")

                // ── Sinks ─────────────────────────────────────────────────
                .WriteTo.Console(
                    outputTemplate:
                        "[{Timestamp:HH:mm:ss} {Level:u3}] {CorrelationId} {Message:lj} " +
                        "{NewLine}{Exception}",
                    restrictedToMinimumLevel: LogEventLevel.Information)

                .WriteTo.File(
                    path:               "logs/gateway-.log",
                    rollingInterval:    RollingInterval.Day,
                    retainedFileCountLimit: 30,
                    outputTemplate:
                        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] " +
                        "CorrelationId={CorrelationId} TraceId={TraceId} UserId={UserId} " +
                        "ServiceName={ServiceName} {Message:lj}{NewLine}{Exception}",
                    restrictedToMinimumLevel: LogEventLevel.Warning)   // file = Warning+ only

                .WriteTo.Seq(
                    serverUrl:               seqSettings.ServerUrl,
                    restrictedToMinimumLevel: LogEventLevel.Information)

                // ── Overrides — suppress noisy framework logs ─────────────
                .MinimumLevel.Override("Microsoft",              LogEventLevel.Warning)
                .MinimumLevel.Override("Microsoft.AspNetCore",   LogEventLevel.Warning)
                .MinimumLevel.Override("Ocelot",                 LogEventLevel.Information)
                .MinimumLevel.Override("System.Net.Http",        LogEventLevel.Warning);
        });

        return builder;
    }
}
