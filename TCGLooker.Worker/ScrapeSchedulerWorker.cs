using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using TCGLooker.Application.Ingestion;

namespace TCGLooker.Worker;

internal sealed class ScrapeSchedulerWorker(
    IConfiguration configuration,
    IEnumerable<IStoreConnector> connectors,
    IScrapeOrchestrator orchestrator,
    IScrapeRepository repository,
    TimeProvider timeProvider,
    ILogger<ScrapeSchedulerWorker> logger) : BackgroundService
{
    private readonly Dictionary<string, DateTimeOffset> _forbiddenUntil =
        new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalMinutes = configuration.GetValue("Scraping:IntervalMinutes", 15);
        var interval = TimeSpan.FromMinutes(Math.Max(1, intervalMinutes));
        var fullIntervalHours = configuration.GetValue("Scraping:FullReconciliationIntervalHours", 24);
        var fullInterval = TimeSpan.FromHours(Math.Max(1, fullIntervalHours));
        var runFullOnStartup = configuration.GetValue("Scraping:RunFullOnStartup", true);
        var lastFullRun = runFullOnStartup ? DateTimeOffset.MinValue : timeProvider.GetUtcNow();

        logger.LogInformation(
            "Scrape scheduler started with {ConnectorCount} connectors and an interval of {IntervalMinutes} minutes",
            connectors.Count(), interval.TotalMinutes);

        var startupSucceeded = await RunCycleAsync(
            runFullOnStartup ? ScrapeMode.Full : ScrapeMode.Incremental,
            stoppingToken);
        if (runFullOnStartup && startupSucceeded)
            lastFullRun = timeProvider.GetUtcNow();

        using var timer = new PeriodicTimer(interval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var now = timeProvider.GetUtcNow();
            var mode = now - lastFullRun >= fullInterval ? ScrapeMode.Full : ScrapeMode.Incremental;
            var succeeded = await RunCycleAsync(mode, stoppingToken);
            if (mode == ScrapeMode.Full && succeeded)
                lastFullRun = now;
        }
    }

    private async Task<bool> RunCycleAsync(ScrapeMode mode, CancellationToken cancellationToken)
    {
        var succeeded = true;
        foreach (var connector in connectors)
        {
            var now = timeProvider.GetUtcNow();
            if (_forbiddenUntil.TryGetValue(connector.Key, out var blockedUntil)
                && blockedUntil > now)
            {
                logger.LogDebug(
                    "Connector {StoreKey} is paused until {BlockedUntil} after an HTTP 403 response",
                    connector.Key,
                    blockedUntil);
                continue;
            }

            try
            {
                await orchestrator.RunAsync(connector, mode, cancellationToken);
            }
            catch (HttpRequestException exception)
                when (exception.StatusCode == HttpStatusCode.Forbidden)
            {
                var retryHours = Math.Max(
                    1,
                    configuration.GetValue("Scraping:ForbiddenRetryHours", 24));
                var retryAt = timeProvider.GetUtcNow().AddHours(retryHours);
                _forbiddenUntil[connector.Key] = retryAt;
                logger.LogWarning(
                    "Connector {StoreKey} returned HTTP 403 and is paused until {RetryAt}. " +
                    "Other connectors will continue normally.",
                    connector.Key,
                    retryAt);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                succeeded = false;
                logger.LogWarning(exception, "Connector {StoreKey} will be retried in the next cycle", connector.Key);
            }
        }

        var retentionDays = Math.Max(1, configuration.GetValue("Scraping:OutOfStockRetentionDays", 30));
        var purged = await repository.PurgeUnavailableAsync(
            timeProvider.GetUtcNow().AddDays(-retentionDays), cancellationToken);
        if (purged > 0)
            logger.LogInformation("Purged {PurgedCount} expired unavailable offers", purged);

        return succeeded;
    }
}
