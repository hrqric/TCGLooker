using System.Text.Json;
using System.Transactions;
using Microsoft.Extensions.Configuration;
using Npgsql;
using TCGLooker.Infra.Postgres;
using Xunit;

namespace TCGLooker.Infra.Tests.Postgres;

public sealed class ProductRefreshPersistenceTests
{
    [Fact]
    public async Task Refresh_selection_respects_age_wishlist_visibility_fresh_observations_and_persisted_attempts()
    {
        var connectionString = Environment.GetEnvironmentVariable("TCGLOOKER_TEST_CONNECTION_STRING");
        if (Environment.GetEnvironmentVariable("TCGLOOKER_TEST_USE_USER_SECRETS") == "true")
            connectionString = new ConfigurationBuilder().AddUserSecrets("e06c7eaf-e101-4cdd-8d31-80e9bc229b66")
                .Build().GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Skip("Configure TCGLOOKER_TEST_CONNECTION_STRING for PostgreSQL verification. All fixtures are rolled back.");
            return;
        }

        var factory = new PostgresConnectionFactory(new PostgresOptions { ConnectionString = connectionString });
        var repository = new PostgresScrapeRepository(factory);
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        var store = Guid.NewGuid();
        var otherStore = Guid.NewGuid();
        var user = Guid.NewGuid();
        var otherUser = Guid.NewGuid();
        var game = Guid.NewGuid();
        var normal = Guid.NewGuid();
        var wished = Guid.NewGuid();
        var normalPrinting = Guid.NewGuid();
        var wishedPrinting = Guid.NewGuid();

        // Repository selection is read-only and joins this ambient transaction.
        // Neither test stores nor offers become visible to the running worker.
        using var scope = new TransactionScope(TransactionScopeOption.RequiresNew,
            new TransactionOptions { IsolationLevel = IsolationLevel.ReadCommitted, Timeout = TimeSpan.FromMinutes(2) },
            TransactionScopeAsyncFlowOption.Enabled);
        await ExecuteAsync("""
            insert into tcglooker.app_user (id) values (@user), (@other_user);
            insert into tcglooker.store (id, slug, name, base_url, connector_key)
            values (@store, @store::text, 'Refresh test', 'https://example.test/' || @store, @store::text),
                   (@other_store, @other_store::text, 'Other', 'https://example.test/' || @other_store, @other_store::text);
            insert into tcglooker.game (id, slug, name) values (@game, @game::text, 'Refresh test');
            insert into tcglooker.card (id, game_id, canonical_name, normalized_name)
            values (@normal, @game, 'Normal', 'normal'), (@wished, @game, 'Wished', 'wished');
            insert into tcglooker.card_printing (id, card_id, language, finish)
            values (@normal_printing, @normal, 'pt-BR', 'normal'), (@wished_printing, @wished, 'pt-BR', 'normal');
            insert into tcglooker.wishlist_item (id, user_id, card_id)
            values (gen_random_uuid(), @user, @wished);
            insert into tcglooker.listing
                (id, store_id, external_id, card_printing_id, title, normalized_title,
                 price_amount, currency, quantity, url, fingerprint, availability, first_seen_at, last_seen_at)
            select gen_random_uuid(), @store, label, printing, label, label, 10, 'BRL', 1,
                   'https://example.test/?view=ecom/item&refid=' || product, label, 'in_stock',
                   @now - interval '1 day', @now - age
            from (values ('a1', 'a', @normal_printing, interval '8 hours'),
                         ('a2', 'a', @normal_printing, interval '7 hours'),
                         ('b', 'b', @wished_printing, interval '4 hours'),
                         ('c', 'c', @wished_printing, interval '1 hour')) as fixture(label, product, printing, age);
            """);

        Assert.EndsWith("refid=b", (await DueAsync(true))!.Url.AbsoluteUri);
        Assert.EndsWith("refid=a", (await DueAsync(false))!.Url.AbsoluteUri);
        Assert.Null(await repository.FindDueAsync(otherStore, now.AddMinutes(-150), now.AddMinutes(-30), true, ct));

        await ExecuteAsync("insert into tcglooker.user_store (user_id, store_id, is_enabled) values (@user, @store, false)");
        Assert.EndsWith("refid=a", (await DueAsync(true))!.Url.AbsoluteUri);
        await ExecuteAsync("update tcglooker.user_store set is_enabled = true where user_id = @user and store_id = @store");

        await ExecuteAsync("update tcglooker.store set scope = 'user', owner_user_id = @other_user where id = @store");
        Assert.EndsWith("refid=a", (await DueAsync(true))!.Url.AbsoluteUri);
        await ExecuteAsync("update tcglooker.store set scope = 'global', owner_user_id = null where id = @store");

        // Discovery has just revisited one variant of product A: do not fetch it again.
        await ExecuteAsync("update tcglooker.listing set last_seen_at = @now where store_id = @store and external_id = 'a1'");
        Assert.EndsWith("refid=b", (await DueAsync(false))!.Url.AbsoluteUri);

        // A failed/abandoned attempt survives repository recreation and provides backoff.
        await ExecuteAsync("""
            insert into tcglooker.scrape_run (id, store_id, started_at, status, mode, cursor)
            values (gen_random_uuid(), @store, @now, 'failed', 'incremental', @cursor)
            """, ("cursor", JsonSerializer.Serialize(new { Kind = "product_refresh", ProductUrl = "https://example.test/?view=ecom/item&refid=b" })));
        repository = new PostgresScrapeRepository(factory);
        Assert.Null(await DueAsync(true));
        var retried = await repository.FindDueAsync(store, now.AddMinutes(-119), now.AddMinutes(1), true, ct);
        Assert.EndsWith("refid=b", retried!.Url.AbsoluteUri);

        Task<TCGLooker.Application.Ingestion.ProductRefreshTarget?> DueAsync(bool priority) =>
            repository.FindDueAsync(store, now.AddMinutes(-150), now.AddMinutes(-30), priority, ct);

        async Task ExecuteAsync(string sql, params (string Name, object Value)[] extra)
        {
            await using var connection = await factory.OpenConnectionAsync(ct);
            await using var command = new NpgsqlCommand(sql, connection);
            foreach (var (key, value) in new (string, object)[]
            {
                ("store", store), ("other_store", otherStore), ("user", user), ("other_user", otherUser),
                ("game", game), ("normal", normal), ("wished", wished), ("normal_printing", normalPrinting),
                ("wished_printing", wishedPrinting), ("now", now)
            }.Concat(extra)) command.Parameters.AddWithValue(key, value);
            await command.ExecuteNonQueryAsync(ct);
        }
    }
}
