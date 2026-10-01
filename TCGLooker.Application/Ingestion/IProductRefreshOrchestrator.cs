namespace TCGLooker.Application.Ingestion;

public interface IProductRefreshOrchestrator
{
    Task RunAsync(
        IProductStoreConnector connector,
        DateTimeOffset yieldAt,
        CancellationToken cancellationToken = default);
}

public interface IProductStoreConnector : IStoreConnector
{
    Task<IReadOnlyCollection<ExternalListing>> FetchProductAsync(
        Uri productUri,
        CancellationToken cancellationToken = default);
}
