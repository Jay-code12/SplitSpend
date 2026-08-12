using Hangfire;
using TransferService.Application.Services;

namespace TransferService.BackgroundJobs;

/// <summary>
/// Registers the transfer timeout check job.
/// Runs every 30 minutes per MVP risk mitigation:
/// "Scheduled status poll every 30 min; auto-reversal after 24h timeout."
/// </summary>
public class HangfireJobRegistrar : IHostedService
{
    private readonly IRecurringJobManager _jobs;
    private readonly ILogger<HangfireJobRegistrar> _log;

    public HangfireJobRegistrar(IRecurringJobManager jobs, ILogger<HangfireJobRegistrar> log)
    {
        _jobs = jobs;
        _log  = log;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _jobs.AddOrUpdate<TimeoutCheckJob>(
            "transfer-timeout-check",
            job => job.RunAsync(CancellationToken.None),
            "*/30 * * * *",  // Every 30 minutes
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        _log.LogInformation(
            "Hangfire job registered: transfer-timeout-check (every 30 minutes)");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

public class TimeoutCheckJob
{
    private readonly TransferApplicationService _svc;

    public TimeoutCheckJob(TransferApplicationService svc) => _svc = svc;

    public async Task RunAsync(CancellationToken ct)
        => await _svc.RunTimeoutCheckAsync(ct);
}
