using TCGLooker.Application.Identity;
using TCGLooker.Application.Watchlist;
using TCGLooker.Domain.Marketplace;
using Xunit;

namespace TCGLooker.Infra.Tests.Watchlist;

public sealed class WishlistServiceTests
{
    [Fact]
    public async Task Create_uses_supabase_subject_to_resolve_internal_user()
    {
        var subject = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var identity = new FakeIdentityRepository(
            new AppUserIdentity(userId, subject.ToString(), true));
        var repository = new FakeWishlistRepository();
        var service = new WishlistService(identity, repository);

        await service.CreateAsync(
            subject.ToString(),
            new CreateWishlistItem(Guid.NewGuid(), null, 25m, "brl", CardCondition.NearMint),
            TestContext.Current.CancellationToken);

        Assert.Equal(subject.ToString(), identity.LastSubject);
        Assert.Equal(userId, repository.LastUserId);
    }

    [Fact]
    public async Task Create_rejects_subject_that_is_not_a_supabase_uuid()
    {
        var identity = new FakeIdentityRepository(
            new AppUserIdentity(Guid.NewGuid(), "invalid", true));
        var service = new WishlistService(identity, new FakeWishlistRepository());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CreateAsync(
            "invalid",
            new CreateWishlistItem(Guid.NewGuid(), null, null, null, null),
            TestContext.Current.CancellationToken));

        Assert.Null(identity.LastSubject);
    }

    [Fact]
    public async Task Create_rejects_price_without_currency()
    {
        var subject = Guid.NewGuid().ToString();
        var identity = new FakeIdentityRepository(
            new AppUserIdentity(Guid.NewGuid(), subject, true));
        var service = new WishlistService(identity, new FakeWishlistRepository());

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(
            subject,
            new CreateWishlistItem(Guid.NewGuid(), null, 10m, null, null),
            TestContext.Current.CancellationToken));

        Assert.Null(identity.LastSubject);
    }

    [Fact]
    public async Task List_rejects_disabled_user()
    {
        var subject = Guid.NewGuid().ToString();
        var identity = new FakeIdentityRepository(
            new AppUserIdentity(Guid.NewGuid(), subject, false));
        var service = new WishlistService(identity, new FakeWishlistRepository());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.ListAsync(subject, false, TestContext.Current.CancellationToken));
    }

    private sealed class FakeIdentityRepository(AppUserIdentity identity) : IUserIdentityRepository
    {
        public string? LastSubject { get; private set; }

        public Task<AppUserIdentity> GetOrCreateAsync(
            string externalAuthId,
            CancellationToken cancellationToken = default)
        {
            LastSubject = externalAuthId;
            return Task.FromResult(identity);
        }
    }

    private sealed class FakeWishlistRepository : IWishlistRepository
    {
        public Guid? LastUserId { get; private set; }

        public Task<WishlistView> CreateAsync(
            Guid userId,
            CreateWishlistItem item,
            CancellationToken cancellationToken = default)
        {
            LastUserId = userId;
            return Task.FromResult(new WishlistView(
                Guid.NewGuid(), item.CardId, "Pikachu", item.CardPrintingId,
                item.MaximumPriceAmount, item.MaximumPriceCurrency,
                item.MinimumCondition, true, DateTimeOffset.UtcNow));
        }

        public Task<IReadOnlyCollection<WishlistView>> ListAsync(
            Guid userId,
            bool includeInactive,
            CancellationToken cancellationToken = default)
        {
            LastUserId = userId;
            return Task.FromResult<IReadOnlyCollection<WishlistView>>([]);
        }

        public Task<bool> DeleteAsync(
            Guid userId,
            Guid wishlistItemId,
            CancellationToken cancellationToken = default)
        {
            LastUserId = userId;
            return Task.FromResult(true);
        }
    }
}

