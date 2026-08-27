using TCGLooker.Domain.Common;
using TCGLooker.Domain.Marketplace;

namespace TCGLooker.Domain.Watchlist;

public sealed class WishlistItem
{
    public required Guid Id { get; init; }
    public required Guid UserId { get; init; }
    public required Guid CardId { get; init; }
    public Guid? CardPrintingId { get; init; }
    public Money? MaximumPrice { get; init; }
    public CardCondition? MinimumCondition { get; init; }
    public bool IsActive { get; private set; } = true;

    public void Disable() => IsActive = false;
}
