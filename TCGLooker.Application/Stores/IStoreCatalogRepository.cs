namespace TCGLooker.Application.Stores;

public interface IStoreCatalogRepository
{
    Task<IReadOnlyCollection<StoreView>> ListVisibleAsync(
        Guid? userId,
        CancellationToken cancellationToken = default);

    Task<bool> SetSelectionAsync(
        Guid userId,
        Guid storeId,
        bool isEnabled,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<StoreSource>> ListEnabledAsync(
        CancellationToken cancellationToken = default);

    Task<StoreRegistrationResult> CreateAsync(
        StoreRegistration registration,
        CancellationToken cancellationToken = default);
}

public sealed record StoreView(
    Guid Id,
    string Slug,
    string Name,
    Uri BaseUrl,
    string ConnectorType,
    StoreScope Scope,
    bool IsEnabled,
    bool IsSelected);

public enum StoreScope
{
    Global,
    User
}

public sealed record StoreSource(
    Guid Id,
    string Slug,
    string Name,
    Uri BaseUrl,
    string ConnectorKey,
    string ConnectorType,
    StoreScope Scope,
    Guid? OwnerUserId);

public sealed record StoreRegistration(
    string ExternalUserId,
    string Slug,
    string Name,
    Uri BaseUrl,
    string ConnectorType,
    StoreScope Scope);

public sealed record StoreRegistrationResult(
    Guid Id,
    string Slug,
    string Name,
    Uri BaseUrl,
    string ConnectorType,
    StoreScope Scope);

public sealed class StoreConflictException(string message) : Exception(message);
