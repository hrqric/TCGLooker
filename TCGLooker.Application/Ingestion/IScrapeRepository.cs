namespace TCGLooker.Application.Ingestion;

public interface IScrapeRepository
{
    Task<IAsyncDisposable?> TryAcquireStoreLeaseAsync(
        Guid storeId,
        CancellationToken cancellationToken = default);

    Task<ScrapeExecution> StartAsync(
        Guid storeId,
        ScrapeMode mode,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default);

    Task<int> UpsertAvailableAsync(
        ScrapeExecution execution,
        IReadOnlyCollection<ExternalListing> listings,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default);

    Task<int> CompleteAsync(
        ScrapeExecution execution,
        IReadOnlyCollection<ExternalListing> unavailableListings,
        int itemsSeen,
        int itemsChanged,
        DateTimeOffset observedAt,
        DateTimeOffset finishedAt,
        CancellationToken cancellationToken = default);

    Task FailAsync(
        ScrapeExecution execution,
        string errorCode,
        int itemsSeen,
        int itemsChanged,
        DateTimeOffset finishedAt,
        CancellationToken cancellationToken = default);

    Task<int> PurgeUnavailableAsync(
        DateTimeOffset olderThan,
        CancellationToken cancellationToken = default);
}

public sealed record ScrapeExecution(Guid RunId, Guid StoreId, ScrapeMode Mode);
