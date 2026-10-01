using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using TCGLooker.Application.Ingestion;
using TCGLooker.Application.Stores;

namespace TCGLooker.Infra.Connectors;

internal sealed class LigaMagicStoreConnector(
    StoreSource source,
    IHttpClientFactory httpClientFactory,
    LigaMagicPageParser parser,
    ILogger<LigaMagicStoreConnector> logger) : IProductStoreConnector
{
    private const int PageSize = 30;

    public Guid StoreId => source.Id;
    public string Key => source.ConnectorKey;

    public async Task<IReadOnlyCollection<ExternalListing>> FetchProductAsync(
        Uri productUri,
        CancellationToken cancellationToken = default)
    {
        if (!StoreAddressPolicy.IsSameOrigin(source.BaseUrl, productUri))
            throw new InvalidDataException("Product refresh points outside the registered store.");
        var referenceId = LigaMagicPageParser.GetQueryValue(productUri, "refid");
        if (string.IsNullOrWhiteSpace(referenceId)
            || LigaMagicPageParser.GetQueryValue(productUri, "view") != "ecom/item")
            throw new InvalidDataException("Product refresh requires a known card detail URL.");
        using var client = httpClientFactory.CreateClient(StoreConnectorTypes.LigaMagic);
        var (html, effectiveUri) = await GetAsync(client, productUri, cancellationToken);
        if (LigaMagicPageParser.GetQueryValue(effectiveUri, "refid") != referenceId)
            throw new InvalidDataException("Product refresh redirected to a different card.");
        var listings = await parser.ParseProductAsync(html, effectiveUri, Key, cancellationToken);
        if (listings.Count == 0)
            throw new InvalidDataException("Product refresh did not return a recognizable card page.");
        // Preserve the discovered URL so redirects to a canonical spelling do not
        // leave the old URL permanently stale in the refresh queue.
        return listings.Select(listing => listing with { Url = productUri }).ToArray();
    }

    public async Task<ScrapePage> FetchAsync(
        ScrapeRequest request,
        CancellationToken cancellationToken = default)
    {
        using var client = httpClientFactory.CreateClient(StoreConnectorTypes.LigaMagic);
        var suffix = request.Mode == ScrapeMode.Incremental ? "&ultimas=1" : string.Empty;
        var listUri = request.PageUri ?? new Uri(source.BaseUrl,
            $"/?view=ecom/itens&tcg=2&txt_estoque=1&txt_limit={PageSize}&txt_order=1&page={request.Page}{suffix}");
        if (!StoreAddressPolicy.IsSameOrigin(source.BaseUrl, listUri))
            throw new InvalidDataException("Catalog cursor points outside the registered store.");
        var (html, effectiveUri) = await GetAsync(client, listUri, cancellationToken);

        if (await parser.IsProductPageAsync(html, cancellationToken))
        {
            var directListings = await parser.ParseProductAsync(html, effectiveUri, Key, cancellationToken);
            if (request.Page != 1 || directListings.Count == 0)
                throw new InvalidDataException("Unexpected product page while following catalog pagination.");
            return new ScrapePage(directListings, null);
        }

        var productUris = (await parser.ParseProductLinksAsync(html, effectiveUri, cancellationToken))
            .Where(uri => StoreAddressPolicy.IsSameOrigin(source.BaseUrl, uri))
            .Distinct()
            .ToArray();
        if (request.Mode == ScrapeMode.Full && productUris.Length == 0)
        {
            throw new InvalidDataException(
                $"Connector {Key} found no Pokémon products on catalog page {request.Page}; progress was preserved.");
        }

        var nextPageUri = await parser.ParseNextPageAsync(html, effectiveUri, request.Page, cancellationToken);
        var fingerprint = productUris.Length == 0 ? null : Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join('\n', productUris.Select(uri => uri.AbsoluteUri).Order(StringComparer.Ordinal)))));
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

        int? nextPage = nextPageUri is not null ? request.Page + 1 : null;
        logger.LogInformation(
            "Connector {StoreKey} parsed {ProductCount} products and {OfferCount} offers from page {Page}",
            Key, productUris.Length, listings.Count, request.Page);
        return new ScrapePage(listings, nextPage, nextPageUri, fingerprint);
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
