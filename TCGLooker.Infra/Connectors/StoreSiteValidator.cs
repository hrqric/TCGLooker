using System.Net;
using System.Net.Sockets;
using TCGLooker.Application.Ingestion;
using TCGLooker.Application.Stores;

namespace TCGLooker.Infra.Connectors;

internal sealed class StoreSiteValidator(LigaMagicPageParser parser, IHttpClientFactory httpClientFactory) : IStoreSiteValidator
{
    private const int MaxResponseBytes = 2 * 1024 * 1024;

    public async Task<StoreSiteValidationResult> ValidateAsync(
        Uri baseUrl,
        string connectorType,
        CancellationToken cancellationToken = default)
    {
        if (!StoreAddressPolicy.TryNormalize(baseUrl, out var normalized, out var addressError))
            return StoreSiteValidationResult.Invalid(addressError!);

        if (!connectorType.Equals(StoreConnectorTypes.LigaMagic, StringComparison.Ordinal))
            return StoreSiteValidationResult.Invalid("Tipo de conector não suportado.");

        try
        {
            using var client = httpClientFactory.CreateClient(StoreConnectorTypes.LigaMagic);

            var current = new Uri(normalized!,
                "/?view=ecom/itens&tcg=2&txt_estoque=1&txt_limit=1&page=1");
            using var response = await client.GetAsync(
                current, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return StoreSiteValidationResult.Invalid($"O site respondeu HTTP {(int)response.StatusCode}.");

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null
                && !mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
                && !mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
                return StoreSiteValidationResult.Invalid("O endereço não retornou uma página HTML.");

            var effectiveUri = response.RequestMessage?.RequestUri ?? current;
            var html = await ReadLimitedAsync(response.Content, cancellationToken);
            var links = await parser.ParseProductLinksAsync(html, effectiveUri, cancellationToken);
            if (links.Count == 0 && !await parser.IsProductPageAsync(html, cancellationToken))
                return StoreSiteValidationResult.Invalid(
                    "O site respondeu, mas não é compatível com o conector LigaMagic.");

            return StoreSiteValidationResult.Valid(normalized!);
        }
        catch (ScrapeDeferredException exception)
        {
            return StoreSiteValidationResult.Invalid(
                $"O site solicitou uma pausa (HTTP {(int?)exception.StatusCode}). Nova tentativa após {exception.RetryAt:O}.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return StoreSiteValidationResult.Invalid("O site excedeu o tempo limite de validação.");
        }
        catch (Exception exception) when (
            exception is HttpRequestException or IOException or SocketException)
        {
            return StoreSiteValidationResult.Invalid("Não foi possível acessar o site com segurança.");
        }
    }

    private static async Task<string> ReadLimitedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaxResponseBytes)
            throw new IOException("The validation response is too large.");

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
                break;
            if (buffer.Length + read > MaxResponseBytes)
                throw new IOException("The validation response is too large.");
            buffer.Write(chunk, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

}

internal static class PublicInternetHttpHandler
{
    public static SocketsHttpHandler Create(bool allowAutoRedirect, ScrapingProxyOptions? proxyOptions = null) => new()
    {
        AllowAutoRedirect = allowAutoRedirect,
        UseProxy = proxyOptions?.Enabled == true,
        Proxy = proxyOptions?.CreateProxy(),
        MaxConnectionsPerServer = 1,
        UseCookies = false,
        MaxAutomaticRedirections = 3,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        ConnectCallback = async (context, cancellationToken) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(
                context.DnsEndPoint.Host, cancellationToken);
            var configuredProxy = proxyOptions?.Enabled == true ? new Uri(proxyOptions.Url!) : null;
            var isProxy = configuredProxy is not null
                && configuredProxy.IdnHost.Equals(context.DnsEndPoint.Host, StringComparison.OrdinalIgnoreCase)
                && configuredProxy.Port == context.DnsEndPoint.Port;
            if (addresses.Length == 0 || (!isProxy && addresses.Any(address => !StoreAddressPolicy.IsPublic(address))))
                throw new HttpRequestException("The site resolves to a non-public address.");

            Exception? lastError = null;
            foreach (var address in addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(
                        new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception exception) when (exception is SocketException or IOException)
                {
                    lastError = exception;
                    socket.Dispose();
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }

            throw new HttpRequestException("The site could not be reached.", lastError);
        }
    };

    public static async Task ValidateDestinationAsync(string host, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        if (addresses.Length == 0 || addresses.Any(address => !StoreAddressPolicy.IsPublic(address)))
            throw new HttpRequestException("The site resolves to a non-public address.");
    }
}

internal static class StoreAddressPolicy
{
    public static bool IsSameOrigin(Uri expectedBaseUrl, Uri candidate) =>
        candidate.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && candidate.Port == 443
        && candidate.IdnHost.Equals(expectedBaseUrl.IdnHost, StringComparison.OrdinalIgnoreCase);

    public static bool TryNormalize(Uri uri, out Uri? normalized, out string? error)
    {
        normalized = null;
        error = null;
        if (!uri.IsAbsoluteUri || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = "O endereço do site deve ser uma URL HTTPS absoluta.";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || uri.Port != 443 || string.IsNullOrWhiteSpace(uri.IdnHost))
        {
            error = "A URL não pode conter credenciais ou uma porta personalizada.";
            return false;
        }

        if (IPAddress.TryParse(uri.IdnHost, out _)
            || uri.IdnHost.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.IdnHost.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            error = "O site deve usar um domínio público.";
            return false;
        }

        normalized = new UriBuilder(Uri.UriSchemeHttps, uri.IdnHost).Uri;
        return true;
    }

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address))
            return false;

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return !address.Equals(IPAddress.IPv6Any)
                && !address.Equals(IPAddress.IPv6None)
                && !address.IsIPv6LinkLocal
                && !address.IsIPv6Multicast
                && !address.IsIPv6SiteLocal
                && (bytes[0] & 0xFE) != 0xFC;

        return bytes[0] != 0
            && bytes[0] != 10
            && bytes[0] != 127
            && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
            && !(bytes[0] == 169 && bytes[1] == 254)
            && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            && !(bytes[0] == 192 && bytes[1] == 168)
            && !(bytes[0] == 198 && bytes[1] is 18 or 19)
            && bytes[0] < 224;
    }
}
