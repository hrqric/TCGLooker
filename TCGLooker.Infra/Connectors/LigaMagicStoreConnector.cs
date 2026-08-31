using Microsoft.Extensions.Logging;
using System.Net;
using TCGLooker.Application.Ingestion;

namespace TCGLooker.Infra.Connectors;

internal sealed class LigaMagicStoreConnector(
    string key,
    IHttpClientFactory httpClientFactory,
    LigaMagicPageParser parser,
    TimeProvider timeProvider,
    ILogger<LigaMagicStoreConnector> logger) : IStoreConnector
{
    private static readonly TimeSpan RequestDelay = TimeSpan.FromSeconds(1);
    private const int PageSize = 120;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private DateTimeOffset _nextRequestAt = DateTimeOffset.MinValue;

    public string Key => key;

    public async Task<ScrapePage> FetchAsync(
        ScrapeRequest request,
        CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient(Key);
        var suffix = request.Mode == ScrapeMode.Incremental ? "&ultimas=1" : string.Empty;
        var listUri = new Uri(client.BaseAddress!,
            $"/?view=ecom/itens&tcg=2&txt_estoque=1&txt_limit={PageSize}&page={request.Page}{suffix}");
        var (html, effectiveUri) = await GetAsync(client, listUri, cancellationToken);

        if (await parser.IsProductPageAsync(html, cancellationToken))
        {
            var directListings = await parser.ParseProductAsync(html, effectiveUri, Key, cancellationToken);
            return new ScrapePage(directListings, null);
        }

        var productUris = await parser.ParseProductLinksAsync(html, effectiveUri, cancellationToken);
        if (request.Mode == ScrapeMode.Full && request.Page == 1 && productUris.Count == 0)
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

        if (productUris.Count > 0 && listings.Count == 0)
        {
            throw new InvalidDataException(
                $"Connector {Key} found products but could not parse any offers.");
        }

        int? nextPage = productUris.Count >= PageSize ? request.Page + 1 : null;
        logger.LogInformation(
            "Connector {StoreKey} parsed {ProductCount} products and {OfferCount} offers from page {Page}",
            Key, productUris.Count, listings.Count, request.Page);
        return new ScrapePage(listings, nextPage);
    }

    private async Task<(string Html, Uri EffectiveUri)> GetAsync(
        HttpClient client,
        Uri uri,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                response = await SendRateLimitedAsync(client, uri, cancellationToken);
            }
            catch (HttpRequestException exception) when (attempt < 3)
            {
                var networkDelay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                logger.LogWarning(
                    exception,
                    "Transient network failure from {StoreKey}; retrying in {RetryDelay}",
                    Key,
                    networkDelay);
                await Task.Delay(networkDelay, cancellationToken);
                continue;
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    var html = await response.Content.ReadAsStringAsync(cancellationToken);
                    return (html, response.RequestMessage?.RequestUri ?? uri);
                }

                var transient = response.StatusCode is HttpStatusCode.RequestTimeout
                    or HttpStatusCode.TooManyRequests
                    or >= HttpStatusCode.InternalServerError;
                if (!transient || attempt >= 3)
                {
                    response.EnsureSuccessStatusCode();
                }

                var retryDelay = response.Headers.RetryAfter?.Delta
                    ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                logger.LogWarning(
                    "Transient HTTP {StatusCode} from {StoreKey}; retrying in {RetryDelay}",
                    (int)response.StatusCode, Key, retryDelay);
                await Task.Delay(retryDelay, cancellationToken);
            }
        }
    }

    private async Task<HttpResponseMessage> SendRateLimitedAsync(
        HttpClient client,
        Uri uri,
        CancellationToken cancellationToken)
    {
        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            var delay = _nextRequestAt - timeProvider.GetUtcNow();
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, timeProvider, cancellationToken);

            var response = await client.GetAsync(
                uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            _nextRequestAt = timeProvider.GetUtcNow() + RequestDelay;
            return response;
        }
        finally
        {
            _requestGate.Release();
        }
    }
}
