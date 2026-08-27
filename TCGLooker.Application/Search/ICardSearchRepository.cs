namespace TCGLooker.Application.Search;

public interface ICardSearchRepository
{
    Task<CardSearchPage> SearchAsync(
        string query,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);
}

public sealed record CardSearchPage(
    IReadOnlyCollection<CardSearchResult> Items,
    int Page,
    int PageSize,
    long Total);

public sealed record CardSearchResult(
    Guid CardId,
    string Name,
    IReadOnlyCollection<ListingSummary> Offers);

public sealed record ListingSummary(
    Guid ListingId,
    string Store,
    decimal Price,
    string Currency,
    string Availability,
    Uri Url,
    DateTimeOffset ObservedAt);
