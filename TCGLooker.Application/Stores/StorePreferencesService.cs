using TCGLooker.Application.Identity;

namespace TCGLooker.Application.Stores;

public sealed class StorePreferencesService(
    IUserIdentityRepository identityRepository,
    IStoreCatalogRepository repository)
{
    public async Task<IReadOnlyCollection<StoreView>> ListAsync(
        string? subject,
        CancellationToken cancellationToken = default)
    {
        Guid? userId = subject is null ? null : await ResolveUserAsync(subject, cancellationToken);
        return await repository.ListVisibleAsync(userId, cancellationToken);
    }

    public async Task<bool> SetSelectionAsync(
        string subject,
        Guid storeId,
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        var userId = await ResolveUserAsync(subject, cancellationToken);
        return await repository.SetSelectionAsync(userId, storeId, isEnabled, cancellationToken);
    }

    private async Task<Guid> ResolveUserAsync(string subject, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(subject, out var parsed))
            throw new UnauthorizedAccessException("O token não contém um sub válido.");

        var identity = await identityRepository.GetOrCreateAsync(parsed.ToString("D"), cancellationToken);
        if (!identity.IsActive)
            throw new UnauthorizedAccessException("O usuário está desativado.");
        return identity.Id;
    }
}
