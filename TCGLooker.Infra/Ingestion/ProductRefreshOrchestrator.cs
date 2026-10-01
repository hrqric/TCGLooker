using System.Net;
using Microsoft.Extensions.Logging;
using TCGLooker.Application.Ingestion;

namespace TCGLooker.Infra.Ingestion;

internal sealed class ProductRefreshOrchestrator(
    IScrapeRepository scrapeRepository,
    IProductRefreshRepository repository,
    ProductRefreshOptions options,
    TimeProvider timeProvider,
    ILogger<ProductRefreshOrchestrator> logger) : IProductRefreshOrchestrator
{
    public async Task RunAsync(
        IProductStoreConnector connector,
        DateTimeOffset yieldAt,
        CancellationToken cancellationToken = default)
    {
        await using var lease = await scrapeRepository.TryAcquireStoreLeaseAsync(connector.StoreId, cancellationToken);
        if (lease is null)
            return;

        for (var index = 0; index < options.MaxProductsPerRun; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = timeProvider.GetUtcNow();
            if (now >= yieldAt)
                break;

            // Start with the oldest product, then reserve every fourth slot for
            // age alone. Wishlist traffic cannot starve the rest of the store.
            var target = await repository.FindDueAsync(
                connector.StoreId,
                now.AddMinutes(-options.StaleAfterMinutes),
                now.AddMinutes(-options.FailureRetryMinutes),
                preferWishlist: index % 4 != 0,
                cancellationToken);
            if (target is null)
                break;

            var execution = await repository.StartProductAsync(
                connector.StoreId, target.Url, timeProvider.GetUtcNow(), cancellationToken);
            try
            {
                var listings = (await connector.FetchProductAsync(target.Url, cancellationToken))
                    .DistinctBy(listing => listing.ExternalId, StringComparer.Ordinal)
                    .ToArray();
                if (listings.Length == 0)
                    throw new InvalidDataException("Product refresh returned no recognizable offers.");
                await repository.SaveProductAsync(execution, listings, timeProvider.GetUtcNow(), cancellationToken);
                logger.LogInformation(
                    "Refreshed product {ProductUrl} for {StoreKey}: {Offers} offers, wishlisted {Wishlisted}, previous observation {LastObservedAt}",
                    target.Url, connector.Key, listings.Length, target.IsWishlisted, target.LastObservedAt);
            }
            catch (Exception exception)
            {
                await scrapeRepository.FailAsync(
                    execution, exception.GetType().Name, 0, 0, timeProvider.GetUtcNow(), CancellationToken.None);
                // Store-wide restrictions and cancellation must stop both routines.
                if (exception is OperationCanceledException or ScrapeDeferredException
                    || exception is HttpRequestException { StatusCode: HttpStatusCode.Forbidden }
                    || exception is not (HttpRequestException or InvalidDataException))
                    throw;

                logger.LogWarning(exception,
                    "Product {ProductUrl} refresh failed; its persisted attempt delays retry while other products continue",
                    target.Url);
            }
        }
    }
}
