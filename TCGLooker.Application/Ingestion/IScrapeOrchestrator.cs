namespace TCGLooker.Application.Ingestion;

public interface IScrapeOrchestrator
{
    Task RunAsync(
        IStoreConnector connector,
        ScrapeMode mode,
        CancellationToken cancellationToken = default);
}
