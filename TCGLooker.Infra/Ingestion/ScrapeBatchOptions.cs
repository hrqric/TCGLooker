namespace TCGLooker.Infra.Ingestion;

internal sealed class ScrapeBatchOptions
{
    public int MaxPagesPerRun { get; set; } = 1;

    public void Validate()
    {
        if (MaxPagesPerRun is < 1 or > 100)
            throw new InvalidOperationException("Scraping:MaxPagesPerRun must be between 1 and 100.");
    }
}
