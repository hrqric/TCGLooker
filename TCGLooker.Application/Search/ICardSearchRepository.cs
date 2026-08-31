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
    string Set,
    string? CollectorNumber,
    string Language,
    string Finish,
    string? Variant,
    string Condition,
    decimal Price,
    string Currency,
    int? Quantity,
    string Availability,
    Uri Url,
    DateTimeOffset ObservedAt);
