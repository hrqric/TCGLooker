namespace TCGLooker.Application.Identity;

public interface IUserIdentityRepository
{
    Task<AppUserIdentity> GetOrCreateAsync(
        string externalAuthId,
        CancellationToken cancellationToken = default);
}

public sealed record AppUserIdentity(Guid Id, string ExternalAuthId, bool IsActive);

