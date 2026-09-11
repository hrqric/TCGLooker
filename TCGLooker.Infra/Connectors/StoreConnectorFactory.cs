using Microsoft.Extensions.Logging;
using TCGLooker.Application.Ingestion;
using TCGLooker.Application.Stores;

namespace TCGLooker.Infra.Connectors;

internal sealed class StoreConnectorFactory(
    IHttpClientFactory httpClientFactory,
    LigaMagicPageParser parser,
    ILoggerFactory loggerFactory) : IStoreConnectorFactory
{
    public IStoreConnector Create(StoreSource source) => source.ConnectorType switch
    {
        StoreConnectorTypes.LigaMagic => new LigaMagicStoreConnector(
            source,
            httpClientFactory,
            parser,
            loggerFactory.CreateLogger<LigaMagicStoreConnector>()),
        _ => throw new NotSupportedException(
            $"Connector type '{source.ConnectorType}' is not supported.")
    };
}

