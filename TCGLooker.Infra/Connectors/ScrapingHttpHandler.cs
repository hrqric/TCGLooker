using System.Net;
using Microsoft.Extensions.Logging;
using TCGLooker.Application.Ingestion;

namespace TCGLooker.Infra.Connectors;

internal sealed class ScrapingHttpHandler(
    ScrapingRequestCoordinator coordinator,
    ScrapingHttpOptions options,
    TimeProvider timeProvider,
    ILogger<ScrapingHttpHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Get || request.RequestUri is not { } original
            || !StoreAddressPolicy.TryNormalize(original, out _, out _))
            throw new HttpRequestException("Scraping only supports GET requests to public HTTPS stores.");

        var current = original;
        var redirects = 0;
        for (var attempt = 0; ;)
        {
            HttpResponseMessage response;
            try
            {
                response = await coordinator.SendAsync(current, async token =>
                {
                    // With a proxy, ConnectCallback sees the proxy instead of the target.
                    if (options.Proxy.Enabled)
                        await PublicInternetHttpHandler.ValidateDestinationAsync(current.IdnHost, token);
                    using var outgoing = new HttpRequestMessage(HttpMethod.Get, current);
                    foreach (var header in request.Headers)
                        outgoing.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    return await base.SendAsync(outgoing, token);
                }, cancellationToken);
            }
            catch (HttpRequestException exception) when (
                exception is not ScrapeDeferredException && attempt < options.MaxRetries)
            {
                await DelayRetryAsync(++attempt, cancellationToken);
                continue;
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested && attempt < options.MaxRetries)
            {
                await DelayRetryAsync(++attempt, cancellationToken);
                continue;
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new HttpRequestException("Scraping request timed out after the configured attempts.", exception);
            }

            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect
                or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                var target = location is null ? null : new Uri(current, location);
                response.Dispose();
                if (++redirects > 3 || target is null || !StoreAddressPolicy.IsSameOrigin(original, target)
                    || !StoreAddressPolicy.TryNormalize(target, out _, out _))
                    throw new HttpRequestException("Scraping redirect is invalid or leaves the registered store origin.");
                current = target;
                continue;
            }

            if ((response.StatusCode == HttpStatusCode.RequestTimeout
                || (int)response.StatusCode >= 500) && attempt < options.MaxRetries)
            {
                var retryAfter = response.Headers.RetryAfter;
                var serverDelay = retryAfter?.Delta ?? (retryAfter?.Date - timeProvider.GetUtcNow());
                response.Dispose();
                await DelayRetryAsync(++attempt, cancellationToken, serverDelay);
                continue;
            }

            return response;
        }
    }

    private async Task DelayRetryAsync(int attempt, CancellationToken cancellationToken, TimeSpan? serverDelay = null)
    {
        var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt))
            + TimeSpan.FromMilliseconds(Random.Shared.Next(options.JitterMilliseconds + 1));
        if (serverDelay > delay)
            delay = serverDelay.Value;
        logger.LogWarning("Transient scraping failure; retry {Attempt} in {RetryDelay}", attempt, delay);
        await Task.Delay(delay, timeProvider, cancellationToken);
    }
}
