using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TCGLooker.Application.Ingestion;

namespace TCGLooker.Worker;

internal sealed class ListingRetentionWorker(
    IConfiguration configuration,
    IScrapeRepository repository,
    TimeProvider timeProvider,
    ILogger<ListingRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await PurgeAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await PurgeAsync(stoppingToken);
    }

    private async Task PurgeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var retentionDays = Math.Max(
                1, configuration.GetValue("Scraping:OutOfStockRetentionDays", 30));
            var purged = await repository.PurgeUnavailableAsync(
                timeProvider.GetUtcNow().AddDays(-retentionDays), cancellationToken);
            if (purged > 0)
                logger.LogInformation("Purged {PurgedCount} expired unavailable offers", purged);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not purge expired unavailable offers");
        }
    }
}
