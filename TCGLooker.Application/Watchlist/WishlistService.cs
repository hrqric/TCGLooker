using TCGLooker.Application.Identity;
using TCGLooker.Domain.Marketplace;

namespace TCGLooker.Application.Watchlist;

public sealed class WishlistService(
    IUserIdentityRepository identityRepository,
    IWishlistRepository wishlistRepository)
{
    public async Task<WishlistView> CreateAsync(
        string subject,
        CreateWishlistItem item,
        CancellationToken cancellationToken = default)
    {
        subject = NormalizeSubject(subject);
        Validate(item);
        var identity = await identityRepository.GetOrCreateAsync(subject, cancellationToken);
        EnsureActive(identity);
        return await wishlistRepository.CreateAsync(identity.Id, item, cancellationToken);
    }

    public async Task<IReadOnlyCollection<WishlistView>> ListAsync(
        string subject,
        bool includeInactive,
        CancellationToken cancellationToken = default)
    {
        subject = NormalizeSubject(subject);
        var identity = await identityRepository.GetOrCreateAsync(subject, cancellationToken);
        EnsureActive(identity);
        return await wishlistRepository.ListAsync(identity.Id, includeInactive, cancellationToken);
    }

    public async Task<bool> DisableAsync(
        string subject,
        Guid wishlistItemId,
        CancellationToken cancellationToken = default)
    {
        subject = NormalizeSubject(subject);
        if (wishlistItemId == Guid.Empty)
            throw new ArgumentException("O identificador da wishlist é inválido.", nameof(wishlistItemId));

        var identity = await identityRepository.GetOrCreateAsync(subject, cancellationToken);
        EnsureActive(identity);
        return await wishlistRepository.DisableAsync(identity.Id, wishlistItemId, cancellationToken);
    }

    private static string NormalizeSubject(string subject)
    {
        if (!Guid.TryParse(subject, out var parsed))
            throw new UnauthorizedAccessException("O token não contém um sub válido do Supabase.");
        return parsed.ToString("D");
    }

    private static void EnsureActive(AppUserIdentity identity)
    {
        if (!identity.IsActive)
            throw new UnauthorizedAccessException("O usuário está desativado.");
    }

    private static void Validate(CreateWishlistItem item)
    {
        if (item.CardId == Guid.Empty)
            throw new ArgumentException("Informe uma carta válida.", nameof(item.CardId));
        if (item.CardPrintingId == Guid.Empty)
            throw new ArgumentException("A impressão da carta é inválida.", nameof(item.CardPrintingId));
        if (item.MaximumPriceAmount is < 0)
            throw new ArgumentException("O preço máximo não pode ser negativo.", nameof(item.MaximumPriceAmount));
        if ((item.MaximumPriceAmount is null) != (item.MaximumPriceCurrency is null))
            throw new ArgumentException("Preço máximo e moeda devem ser informados juntos.");
        if (item.MaximumPriceCurrency is not null
            && (item.MaximumPriceCurrency.Length != 3
                || item.MaximumPriceCurrency.Any(character => !char.IsAsciiLetter(character))))
        {
            throw new ArgumentException("A moeda deve possuir três letras, como BRL.");
        }
        if (item.MinimumCondition == CardCondition.Unknown)
            throw new ArgumentException("A condição mínima não pode ser unknown.");
    }
}
