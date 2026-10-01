namespace TCGLooker.Infra.Ingestion;

internal sealed class ProductRefreshOptions
{
    public int StaleAfterMinutes { get; set; } = 150;
    public int MaxProductsPerRun { get; set; } = 10;
    public int FailureRetryMinutes { get; set; } = 30;

    public void Validate()
    {
        if (StaleAfterMinutes is < 1 or > 10080 || MaxProductsPerRun is < 4 or > 100
            || FailureRetryMinutes is < 1 or > 1440)
            throw new InvalidOperationException("Invalid Scraping:Refresh age, batch size (4-100) or retry interval.");
    }
}
