using TCGLooker.Domain.Common;
using TCGLooker.Domain.Marketplace;

namespace TCGLooker.Application.Ingestion;

public interface IStoreConnector
{
    string Key { get; }

    Task<ScrapePage> FetchAsync(
        ScrapeCursor? cursor,
        CancellationToken cancellationToken = default);
}

public sealed record ScrapeCursor(string Value);

public sealed record ScrapePage(
    IReadOnlyCollection<ExternalListing> Listings,
    ScrapeCursor? NextCursor);

public sealed record ExternalListing(
    string ExternalId,
    string Title,
    Uri Url,
    Money Price,
    CardCondition Condition,
    int? Quantity,
    IReadOnlyDictionary<string, string> Attributes);
