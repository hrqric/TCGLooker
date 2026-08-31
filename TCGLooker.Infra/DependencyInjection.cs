using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TCGLooker.Application.Ingestion;
using TCGLooker.Application.Search;
using TCGLooker.Infra.Connectors;
using TCGLooker.Infra.Health;
using TCGLooker.Infra.Ingestion;
using TCGLooker.Infra.Postgres;

namespace TCGLooker.Infra;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton(new PostgresOptions
        {
            ConnectionString = configuration.GetConnectionString(PostgresOptions.ConnectionStringName)
        });
        services.AddSingleton<PostgresConnectionFactory>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<LigaMagicPageParser>();
        services.AddSingleton<IScrapeRepository, PostgresScrapeRepository>();
        services.AddSingleton<IScrapeOrchestrator, ScrapeOrchestrator>();
        services.AddSingleton<ICardSearchRepository, PostgresCardSearchRepository>();
        services.AddHealthChecks()
            .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"]);
        services.AddHttpClient(StoreConnectorKeys.CardsHall, client =>
        {
            client.BaseAddress = new Uri("https://www.cardshall.com.br");
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("TCGLooker/0.1");
        });

        services.AddHttpClient(StoreConnectorKeys.TabletopTcg, client =>
        {
            client.BaseAddress = new Uri("https://www.tabletoptcg.com.br");
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("TCGLooker/0.1");
        });

        services.AddSingleton<IStoreConnector>(serviceProvider =>
            new LigaMagicStoreConnector(
                StoreConnectorKeys.CardsHall,
                serviceProvider.GetRequiredService<IHttpClientFactory>(),
                serviceProvider.GetRequiredService<LigaMagicPageParser>(),
                serviceProvider.GetRequiredService<TimeProvider>(),
                serviceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<LigaMagicStoreConnector>>()));
        services.AddSingleton<IStoreConnector>(serviceProvider =>
            new LigaMagicStoreConnector(
                StoreConnectorKeys.TabletopTcg,
                serviceProvider.GetRequiredService<IHttpClientFactory>(),
                serviceProvider.GetRequiredService<LigaMagicPageParser>(),
                serviceProvider.GetRequiredService<TimeProvider>(),
                serviceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<LigaMagicStoreConnector>>()));

        return services;
    }
}
