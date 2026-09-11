namespace TCGLooker.Application.Stores;

public interface IStoreSiteValidator
{
    Task<StoreSiteValidationResult> ValidateAsync(
        Uri baseUrl,
        string connectorType,
        CancellationToken cancellationToken = default);
}

public sealed record StoreSiteValidationResult(bool IsValid, Uri? CanonicalBaseUrl, string? Error)
{
    public static StoreSiteValidationResult Valid(Uri canonicalBaseUrl) =>
        new(true, canonicalBaseUrl, null);

    public static StoreSiteValidationResult Invalid(string error) =>
        new(false, null, error);
}
