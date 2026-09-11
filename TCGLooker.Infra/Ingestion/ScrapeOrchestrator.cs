using Microsoft.Extensions.Logging;
using TCGLooker.Application.Ingestion;

namespace TCGLooker.Infra.Ingestion;

internal sealed class ScrapeOrchestrator(
    IScrapeRepository repository,
    TimeProvider timeProvider,
    ILogger<ScrapeOrchestrator> logger) : IScrapeOrchestrator
{
    public async Task RunAsync(
        IStoreConnector connector,
        ScrapeMode mode,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await repository.TryAcquireStoreLeaseAsync(
            connector.StoreId, cancellationToken);
        if (lease is null)
        {
            logger.LogInformation(
                "Scrape for {StoreKey} skipped because another worker owns its lease",
                connector.Key);
            return;
        }

        var execution = await repository.StartAsync(
            connector.StoreId,
            mode,
            timeProvider.GetUtcNow(),
            cancellationToken);
        var itemsSeen = 0;
        var itemsChanged = 0;

        try
        {
            var unavailableListings = new List<ExternalListing>();
            int? page = 1;
            while (page is not null)
            {
                var result = await connector.FetchAsync(
                    new ScrapeRequest(mode, page.Value), cancellationToken);
                var uniqueListings = result.Listings
                    .DistinctBy(listing => listing.ExternalId, StringComparer.Ordinal)
                    .ToArray();
                var available = uniqueListings
                    .Where(listing => listing.Quantity > 0)
                    .ToArray();
                unavailableListings.AddRange(
                    uniqueListings.Where(listing => listing.Quantity == 0));
                itemsSeen += uniqueListings.Length;
                itemsChanged += await repository.UpsertAvailableAsync(
                    execution,
                    available,
                    timeProvider.GetUtcNow(),
                    cancellationToken);
                page = result.NextPage;
            }

            var observedAt = timeProvider.GetUtcNow();
            var uniqueUnavailableListings = unavailableListings
                .DistinctBy(listing => listing.ExternalId, StringComparer.Ordinal)
                .ToArray();
            itemsChanged += await repository.CompleteAsync(
                execution,
                uniqueUnavailableListings,
                itemsSeen,
                itemsChanged,
                observedAt,
                observedAt,
                cancellationToken);
            logger.LogInformation(
                "Scrape {Mode} for {StoreKey} completed with {ItemsSeen} offers",
                mode, connector.Key, itemsSeen);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await repository.FailAsync(
                execution,
                exception.GetType().Name,
                itemsSeen,
                itemsChanged,
                timeProvider.GetUtcNow(),
                CancellationToken.None);
            logger.LogError(exception, "Scrape {Mode} for {StoreKey} failed", mode, connector.Key);
            throw;
        }
    }
}
