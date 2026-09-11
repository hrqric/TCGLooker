using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TCGLooker.Application.Ingestion;
using TCGLooker.Application.Stores;

namespace TCGLooker.Worker;

internal sealed class ScrapeSchedulerWorker(
    IConfiguration configuration,
    IStoreCatalogRepository storeCatalog,
    IStoreConnectorFactory connectorFactory,
    IScrapeOrchestrator orchestrator,
    TimeProvider timeProvider,
    ILogger<ScrapeSchedulerWorker> logger) : BackgroundService
{
    private readonly Dictionary<Guid, RunningStoreWorker> _workers = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var refreshSeconds = Math.Max(
            10, configuration.GetValue("Scraping:StoreRefreshSeconds", 30));
        logger.LogInformation(
            "Store worker supervisor started with a refresh interval of {RefreshSeconds} seconds",
            refreshSeconds);

        try
        {
            await SynchronizeWorkersAsync(stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(refreshSeconds));
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await SynchronizeWorkersAsync(stoppingToken);
        }
        finally
        {
            foreach (var worker in _workers.Values)
                worker.Cancellation.Cancel();
            await Task.WhenAll(_workers.Values.Select(worker => worker.Task));
            foreach (var worker in _workers.Values)
                worker.Cancellation.Dispose();
        }
    }

    private async Task SynchronizeWorkersAsync(CancellationToken cancellationToken)
    {
        IReadOnlyCollection<StoreSource> enabledStores;
        try
        {
            enabledStores = await storeCatalog.ListEnabledAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Could not refresh the enabled store catalog");
            return;
        }

        var current = enabledStores.ToDictionary(store => store.Id);
        foreach (var (storeId, running) in _workers.ToArray())
        {
            if (current.TryGetValue(storeId, out var source)
                && source == running.Source
                && !running.Task.IsCompleted)
            {
                continue;
            }

            running.Cancellation.Cancel();
            try
            {
                await running.Task;
            }
            catch (OperationCanceledException)
            {
                // Expected when a store is disabled or reconfigured.
            }
            running.Cancellation.Dispose();
            _workers.Remove(storeId);
            logger.LogInformation("Stopped dedicated worker for store {StoreKey}", running.Source.ConnectorKey);
        }

        foreach (var source in enabledStores)
        {
            if (_workers.ContainsKey(source.Id))
                continue;

            try
            {
                var connector = connectorFactory.Create(source);
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var task = RunStoreAsync(source, connector, cancellation.Token);
                _workers[source.Id] = new RunningStoreWorker(source, cancellation, task);
                logger.LogInformation("Started dedicated worker for store {StoreKey}", source.ConnectorKey);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Could not start worker for store {StoreKey}", source.ConnectorKey);
            }
        }

        logger.LogDebug("Worker supervisor has {WorkerCount} active store workers", _workers.Count);
    }

    private async Task RunStoreAsync(
        StoreSource source,
        IStoreConnector connector,
        CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(
            1, configuration.GetValue("Scraping:IntervalMinutes", 15)));
        var fullInterval = TimeSpan.FromHours(Math.Max(
            1, configuration.GetValue("Scraping:FullReconciliationIntervalHours", 24)));
        var runFullOnStartup = configuration.GetValue("Scraping:RunFullOnStartup", false);
        var lastFullRun = runFullOnStartup ? DateTimeOffset.MinValue : timeProvider.GetUtcNow();
        var forbiddenUntil = DateTimeOffset.MinValue;

        var startupSucceeded = await RunCycleAsync(
            connector,
            runFullOnStartup ? ScrapeMode.Full : ScrapeMode.Incremental,
            forbiddenUntil,
            value => forbiddenUntil = value,
            cancellationToken);
        if (runFullOnStartup && startupSucceeded)
            lastFullRun = timeProvider.GetUtcNow();

        while (!cancellationToken.IsCancellationRequested)
        {
            // Wait after completion; a slow crawl must not trigger a queued cycle immediately.
            await Task.Delay(interval, timeProvider, cancellationToken);
            var now = timeProvider.GetUtcNow();
            var mode = now - lastFullRun >= fullInterval ? ScrapeMode.Full : ScrapeMode.Incremental;
            var succeeded = await RunCycleAsync(
                connector, mode, forbiddenUntil, value => forbiddenUntil = value, cancellationToken);
            if (mode == ScrapeMode.Full && succeeded)
                lastFullRun = now;
        }

        logger.LogDebug("Dedicated worker loop ended for store {StoreKey}", source.ConnectorKey);
    }

    private async Task<bool> RunCycleAsync(
        IStoreConnector connector,
        ScrapeMode mode,
        DateTimeOffset forbiddenUntil,
        Action<DateTimeOffset> setForbiddenUntil,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (forbiddenUntil > now)
        {
            logger.LogDebug(
                "Connector {StoreKey} is paused until {BlockedUntil} after a store restriction",
                connector.Key,
                forbiddenUntil);
            return false;
        }

        try
        {
            await orchestrator.RunAsync(connector, mode, cancellationToken);
            return true;
        }
        catch (ScrapeDeferredException exception)
        {
            setForbiddenUntil(exception.RetryAt);
            logger.LogWarning(
                "Connector {StoreKey} returned HTTP {StatusCode} and is paused until {RetryAt}",
                connector.Key, (int?)exception.StatusCode, exception.RetryAt);
            return false;
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.Forbidden)
        {
            var retryHours = Math.Max(
                1, configuration.GetValue("Scraping:ForbiddenRetryHours", 24));
            var retryAt = timeProvider.GetUtcNow().AddHours(retryHours);
            setForbiddenUntil(retryAt);
            logger.LogWarning(
                "Connector {StoreKey} returned HTTP 403 and is paused until {RetryAt}",
                connector.Key,
                retryAt);
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Connector {StoreKey} will be retried in the next cycle", connector.Key);
            return false;
        }
    }

    private sealed record RunningStoreWorker(
        StoreSource Source,
        CancellationTokenSource Cancellation,
        Task Task);
}
