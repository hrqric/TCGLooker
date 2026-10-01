using Microsoft.Extensions.Logging;
using TCGLooker.Application.Ingestion;

namespace TCGLooker.Infra.Ingestion;

internal sealed class ScrapeOrchestrator(
    IScrapeRepository repository,
    TimeProvider timeProvider,
    ILogger<ScrapeOrchestrator> logger,
    ScrapeBatchOptions options) : IScrapeOrchestrator
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
            var progress = execution.Progress;
            var fingerprints = new HashSet<string>(progress.PageFingerprints ?? [], StringComparer.Ordinal);
            var pagesProcessed = 0;
            while (progress.NextPage is int page && pagesProcessed < options.MaxPagesPerRun)
            {
                var result = await connector.FetchAsync(
                    new ScrapeRequest(mode, page, progress.NextPageUri), cancellationToken);
                if (result.NextPage is int nextPage && nextPage != page + 1)
                    throw new InvalidDataException("Catalog pagination did not advance to the next page.");
                if (result.Fingerprint is not null && !fingerprints.Add(result.Fingerprint))
                    throw new InvalidDataException("Catalog repeated a previously processed page; progress was preserved.");
                var uniqueListings = result.Listings
                    .DistinctBy(listing => listing.ExternalId, StringComparer.Ordinal)
                    .ToArray();
                var nextProgress = progress with
                {
                    NextPage = result.NextPage,
                    NextPageUri = result.NextPageUri,
                    PageFingerprints = fingerprints.ToArray()
                };
                var changed = await repository.SavePageAsync(
                    execution,
                    uniqueListings,
                    nextProgress,
                    itemsSeen + uniqueListings.Length,
                    itemsChanged,
                    timeProvider.GetUtcNow(),
                    cancellationToken);
                itemsSeen += uniqueListings.Length;
                itemsChanged += changed;
                progress = nextProgress;
                pagesProcessed++;
            }

            await repository.CompleteAsync(
                execution,
                progress,
                itemsSeen,
                itemsChanged,
                timeProvider.GetUtcNow(),
                cancellationToken);
            logger.LogInformation(
                "Scrape {Mode} for {StoreKey}: cycle {CycleId}, {PagesProcessed} pages, {ItemsSeen} offers, next page {NextPage}, cycle complete {CycleComplete}",
                mode, connector.Key, progress.CycleId, pagesProcessed, itemsSeen,
                progress.NextPage, progress.NextPage is null);
        }
        catch (Exception exception)
        {
            await repository.FailAsync(
                execution,
                exception.GetType().Name,
                itemsSeen,
                itemsChanged,
                timeProvider.GetUtcNow(),
                CancellationToken.None);
            if (exception is not OperationCanceledException)
                logger.LogError(exception, "Scrape {Mode} for {StoreKey} failed", mode, connector.Key);
            throw;
        }
    }
}
