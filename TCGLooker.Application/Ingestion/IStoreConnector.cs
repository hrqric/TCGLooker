using TCGLooker.Domain.Common;
using TCGLooker.Domain.Marketplace;

namespace TCGLooker.Application.Ingestion;

public interface IStoreConnector
{
    string Key { get; }

    Task<ScrapePage> FetchAsync(
        ScrapeRequest request,
        CancellationToken cancellationToken = default);
}

public enum ScrapeMode
{
    Incremental,
    Full
}

public sealed record ScrapeRequest(ScrapeMode Mode, int Page = 1);

public sealed record ScrapePage(
    IReadOnlyCollection<ExternalListing> Listings,
    int? NextPage);

public sealed record ExternalListing(
    string ExternalId,
    string CardName,
    string SetExternalCode,
    string SetName,
    string? CollectorNumber,
    string Language,
    CardFinish Finish,
    string? Variant,
    string Title,
    Uri Url,
    Money Price,
    CardCondition Condition,
    int? Quantity,
    IReadOnlyDictionary<string, string> Attributes);
