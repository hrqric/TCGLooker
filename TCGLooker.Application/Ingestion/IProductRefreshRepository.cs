namespace TCGLooker.Application.Ingestion;

public interface IProductRefreshRepository
{
    Task<ProductRefreshTarget?> FindDueAsync(
        Guid storeId,
        DateTimeOffset staleBefore,
        DateTimeOffset retryBefore,
        bool preferWishlist,
        CancellationToken cancellationToken = default);

    Task<ScrapeExecution> StartProductAsync(
        Guid storeId,
        Uri productUri,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default);

    // A successful observation and its audit record commit together.
    Task SaveProductAsync(
        ScrapeExecution execution,
        IReadOnlyCollection<ExternalListing> listings,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default);
}

public sealed record ProductRefreshTarget(Uri Url, DateTimeOffset LastObservedAt, bool IsWishlisted);
