using TCGLooker.Domain.Marketplace;

namespace TCGLooker.Application.Watchlist;

public interface IWishlistRepository
{
    Task<WishlistView> CreateAsync(
        Guid userId,
        CreateWishlistItem item,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyCollection<WishlistView>> ListAsync(
        Guid userId,
        bool includeInactive,
        CancellationToken cancellationToken = default);

    Task<bool> DisableAsync(
        Guid userId,
        Guid wishlistItemId,
        CancellationToken cancellationToken = default);
}

public sealed record CreateWishlistItem(
    Guid CardId,
    Guid? CardPrintingId,
    decimal? MaximumPriceAmount,
    string? MaximumPriceCurrency,
    CardCondition? MinimumCondition);

public sealed record WishlistView(
    Guid Id,
    Guid CardId,
    string CardName,
    Guid? CardPrintingId,
    decimal? MaximumPriceAmount,
    string? MaximumPriceCurrency,
    CardCondition? MinimumCondition,
    bool IsActive,
    DateTimeOffset CreatedAt);

public sealed class WishlistTargetNotFoundException(string message) : Exception(message);
public sealed class WishlistConflictException(string message) : Exception(message);

