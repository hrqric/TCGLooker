using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TCGLooker.Worker;

internal sealed class ScrapeSchedulerWorker(
    IConfiguration configuration,
    ILogger<ScrapeSchedulerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalMinutes = configuration.GetValue("Scraping:IntervalMinutes", 15);
        var interval = TimeSpan.FromMinutes(Math.Max(1, intervalMinutes));

        logger.LogInformation(
            "Scrape scheduler started with an interval of {IntervalMinutes} minutes. No connectors are registered yet.",
            interval.TotalMinutes);

        using var timer = new PeriodicTimer(interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
            logger.LogDebug("Scrape scheduler tick. No connectors are registered yet.");
    }
}
