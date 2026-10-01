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

    // Offers and the next page must commit together: never skip an uncommitted page.
    Task<int> SavePageAsync(
        ScrapeExecution execution,
        IReadOnlyCollection<ExternalListing> listings,
        ScrapeProgress progress,
        int itemsSeen,
        int itemsChanged,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default);

    Task CompleteAsync(
        ScrapeExecution execution,
        ScrapeProgress progress,
        int itemsSeen,
        int itemsChanged,
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

public sealed record ScrapeExecution(Guid RunId, Guid StoreId, ScrapeMode Mode, ScrapeProgress Progress);

public sealed record ScrapeProgress(
    Guid CycleId,
    DateTimeOffset CycleStartedAt,
    int? NextPage = 1,
    Uri? NextPageUri = null,
    string[]? PageFingerprints = null);
