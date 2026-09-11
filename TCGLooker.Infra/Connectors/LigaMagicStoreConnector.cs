using Microsoft.Extensions.Logging;
using TCGLooker.Application.Ingestion;
using TCGLooker.Application.Stores;

namespace TCGLooker.Infra.Connectors;

internal sealed class LigaMagicStoreConnector(
    StoreSource source,
    IHttpClientFactory httpClientFactory,
    LigaMagicPageParser parser,
    ILogger<LigaMagicStoreConnector> logger) : IStoreConnector
{
    private const int PageSize = 120;

    public Guid StoreId => source.Id;
    public string Key => source.ConnectorKey;

    public async Task<ScrapePage> FetchAsync(
        ScrapeRequest request,
        CancellationToken cancellationToken = default)
    {
        using var client = httpClientFactory.CreateClient(StoreConnectorTypes.LigaMagic);
        var suffix = request.Mode == ScrapeMode.Incremental ? "&ultimas=1" : string.Empty;
        var listUri = new Uri(source.BaseUrl,
            $"/?view=ecom/itens&tcg=2&txt_estoque=1&txt_limit={PageSize}&page={request.Page}{suffix}");
        var (html, effectiveUri) = await GetAsync(client, listUri, cancellationToken);

        if (await parser.IsProductPageAsync(html, cancellationToken))
        {
            var directListings = await parser.ParseProductAsync(html, effectiveUri, Key, cancellationToken);
            return new ScrapePage(directListings, null);
        }

        var productUris = (await parser.ParseProductLinksAsync(html, effectiveUri, cancellationToken))
            .Where(uri => StoreAddressPolicy.IsSameOrigin(source.BaseUrl, uri))
            .Distinct()
            .ToArray();
        if (request.Mode == ScrapeMode.Full && request.Page == 1 && productUris.Length == 0)
        {
            throw new InvalidDataException(
                $"Connector {Key} found no Pokémon products on the first full-crawl page.");
        }

        var listings = new List<ExternalListing>();
        foreach (var productUri in productUris)
        {
            var (productHtml, finalProductUri) = await GetAsync(client, productUri, cancellationToken);
            listings.AddRange(await parser.ParseProductAsync(
                productHtml, finalProductUri, Key, cancellationToken));
        }

        if (productUris.Length > 0 && listings.Count == 0)
        {
            throw new InvalidDataException(
                $"Connector {Key} found products but could not parse any offers.");
        }

        int? nextPage = productUris.Length >= PageSize ? request.Page + 1 : null;
        logger.LogInformation(
            "Connector {StoreKey} parsed {ProductCount} products and {OfferCount} offers from page {Page}",
            Key, productUris.Length, listings.Count, request.Page);
        return new ScrapePage(listings, nextPage);
    }

    private async Task<(string Html, Uri EffectiveUri)> GetAsync(
        HttpClient client,
        Uri uri,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        var effectiveUri = response.RequestMessage?.RequestUri ?? uri;
        if (!StoreAddressPolicy.IsSameOrigin(source.BaseUrl, effectiveUri))
            throw new HttpRequestException("Connector navigation left the registered store origin.");
        return (await response.Content.ReadAsStringAsync(cancellationToken), effectiveUri);
    }
}
