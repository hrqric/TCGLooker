using TCGLooker.Application.Stores;

namespace TCGLooker.Application.Ingestion;

public interface IStoreConnectorFactory
{
    IStoreConnector Create(StoreSource source);
}

public static class StoreConnectorTypes
{
    public const string LigaMagic = "liga_magic";
}
