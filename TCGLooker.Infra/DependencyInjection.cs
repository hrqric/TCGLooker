using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TCGLooker.Application.Ingestion;
using TCGLooker.Application.Identity;
using TCGLooker.Application.Notifications;
using TCGLooker.Application.Search;
using TCGLooker.Application.Stores;
using TCGLooker.Application.Watchlist;
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
        var scrapingOptions = new ScrapingHttpOptions();
        configuration.GetSection("Scraping").Bind(scrapingOptions);
        scrapingOptions.Validate();
        services.AddSingleton(scrapingOptions);
        var batchOptions = new ScrapeBatchOptions();
        configuration.GetSection("Scraping").Bind(batchOptions);
        batchOptions.Validate();
        services.AddSingleton(batchOptions);
        var refreshOptions = new ProductRefreshOptions();
        configuration.GetSection("Scraping:Refresh").Bind(refreshOptions);
        refreshOptions.Validate();
        services.AddSingleton(refreshOptions);
        services.AddSingleton<ScrapingRequestCoordinator>();
        services.AddTransient<ScrapingHttpHandler>();
        services.AddSingleton<LigaMagicPageParser>();
        services.AddSingleton<PostgresScrapeRepository>();
        services.AddSingleton<IScrapeRepository>(provider => provider.GetRequiredService<PostgresScrapeRepository>());
        services.AddSingleton<IProductRefreshRepository>(provider => provider.GetRequiredService<PostgresScrapeRepository>());
        services.AddSingleton<IScrapeOrchestrator, ScrapeOrchestrator>();
        services.AddSingleton<IProductRefreshOrchestrator, ProductRefreshOrchestrator>();
        services.AddSingleton<IStoreCatalogRepository, PostgresStoreCatalogRepository>();
        services.AddSingleton<StorePreferencesService>();
        services.AddSingleton<IStoreConnectorFactory, StoreConnectorFactory>();
        services.AddSingleton<IStoreSiteValidator, StoreSiteValidator>();
        services.AddSingleton<ICardSearchRepository, PostgresCardSearchRepository>();
        services.AddSingleton<IUserIdentityRepository, PostgresUserIdentityRepository>();
        services.AddSingleton<IWishlistRepository, PostgresWishlistRepository>();
        services.AddSingleton<WishlistService>();
        services.AddSingleton<INotificationOutboxProcessor, PostgresNotificationOutboxProcessor>();
        services.AddHealthChecks()
            .AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"]);
        services.AddHttpClient(StoreConnectorTypes.LigaMagic, client =>
        {
            // Queue/cooldown waits must not consume the per-request network timeout.
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd(scrapingOptions.UserAgent);
        }).AddHttpMessageHandler<ScrapingHttpHandler>()
            .ConfigurePrimaryHttpMessageHandler(
                () => PublicInternetHttpHandler.Create(allowAutoRedirect: false, scrapingOptions.Proxy));

        return services;
    }
}
