using Hangfire;

namespace BudgetService.BackgroundJobs;

/// <summary>
/// Registers Hangfire recurring jobs on startup.
/// DailyRelease: 00:01 UTC every day — allocate daily budgets.
/// DailyExpiry:  23:55 UTC every day — return unused daily funds to Main Balance.
///
/// MVP Alert: If DailyRelease does not fire by 06:10 AM → Critical alert (configured
/// in Application Insights from the MVP doc).
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

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Daily Release — 00:01 UTC
        _jobs.AddOrUpdate<DailyCronJob>(
            "daily-budget-release",
            job => job.RunDailyReleaseAsync(CancellationToken.None),
            "1 0 * * *",                 // cron: 00:01 UTC daily
            new RecurringJobOptions
            {
                TimeZone = TimeZoneInfo.Utc
            });

        // Daily Expiry — 23:55 UTC
        _jobs.AddOrUpdate<DailyCronJob>(
            "daily-budget-expiry",
            job => job.RunDailyExpiryAsync(CancellationToken.None),
            "55 23 * * *",               // cron: 23:55 UTC daily
            new RecurringJobOptions
            {
                TimeZone = TimeZoneInfo.Utc
            });

        _log.LogInformation(
            "Hangfire recurring jobs registered: daily-budget-release (00:01 UTC), daily-budget-expiry (23:55 UTC)");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Thin Hangfire job wrapper. Resolves the scoped DailyCronService from DI per execution.
/// </summary>
public class DailyCronJob
{
    private readonly Application.Services.DailyCronService _cronService;

    public DailyCronJob(Application.Services.DailyCronService cronService)
    {
        _cronService = cronService;
    }

    public async Task RunDailyReleaseAsync(CancellationToken ct)
        => await _cronService.RunDailyReleaseAsync(ct);

    public async Task RunDailyExpiryAsync(CancellationToken ct)
        => await _cronService.RunDailyExpiryAsync(ct);
}
