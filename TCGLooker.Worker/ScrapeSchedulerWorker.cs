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
    IProductRefreshOrchestrator productRefresh,
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

    internal async Task RunStoreAsync(
        StoreSource source,
        IStoreConnector connector,
        CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(
            1, configuration.GetValue("Scraping:IntervalMinutes", 15)));
        var refreshInterval = TimeSpan.FromMinutes(Math.Max(
            1, configuration.GetValue("Scraping:Refresh:PollingMinutes", 5)));
        var refreshEnabled = configuration.GetValue("Scraping:Refresh:Enabled", true)
            && connector is IProductStoreConnector;
        var forbiddenUntil = DateTimeOffset.MinValue;
        var nextDiscovery = timeProvider.GetUtcNow();
        var nextRefresh = nextDiscovery;

        while (!cancellationToken.IsCancellationRequested)
        {
            var now = timeProvider.GetUtcNow();
            if (forbiddenUntil > now)
            {
                await Task.Delay(forbiddenUntil - now, timeProvider, cancellationToken);
                continue;
            }

            // Discovery always wins when both routines are due. Refresh yields
            // between products at its deadline instead of occupying the store indefinitely.
            if (now >= nextDiscovery)
            {
                await RunCycleAsync(connector,
                    token => orchestrator.RunAsync(connector, ScrapeMode.Full, token),
                    value => forbiddenUntil = value, cancellationToken);
                nextDiscovery = timeProvider.GetUtcNow().Add(interval);
                continue;
            }

            if (refreshEnabled && now >= nextRefresh)
            {
                await RunCycleAsync(connector,
                    token => productRefresh.RunAsync((IProductStoreConnector)connector, nextDiscovery, token),
                    value => forbiddenUntil = value, cancellationToken);
                nextRefresh = timeProvider.GetUtcNow().Add(refreshInterval);
                continue;
            }

            var nextWork = refreshEnabled && nextRefresh < nextDiscovery ? nextRefresh : nextDiscovery;
            await Task.Delay(nextWork - now, timeProvider, cancellationToken);
        }

        logger.LogDebug("Dedicated worker loop ended for store {StoreKey}", source.ConnectorKey);
    }

    private async Task RunCycleAsync(
        IStoreConnector connector,
        Func<CancellationToken, Task> run,
        Action<DateTimeOffset> setForbiddenUntil,
        CancellationToken cancellationToken)
    {
        try
        {
            await run(cancellationToken);
        }
        catch (ScrapeDeferredException exception)
        {
            setForbiddenUntil(exception.RetryAt);
            logger.LogWarning(
                "Connector {StoreKey} returned HTTP {StatusCode} and is paused until {RetryAt}",
                connector.Key, (int?)exception.StatusCode, exception.RetryAt);
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
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Connector {StoreKey} will be retried in the next cycle", connector.Key);
        }
    }

    private sealed record RunningStoreWorker(
        StoreSource Source,
        CancellationTokenSource Cancellation,
        Task Task);
}
