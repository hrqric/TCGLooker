using System.Transactions;
using Microsoft.Extensions.Configuration;
using Npgsql;
using TCGLooker.Infra.Postgres;
using Xunit;

namespace TCGLooker.Infra.Tests.Postgres;

public sealed class PreferencesPersistenceTests
{
    [Fact]
    public async Task Preferences_and_wishlist_preserve_ownership_and_delete_only_the_wishlist()
    {
        var connectionString = Environment.GetEnvironmentVariable("TCGLOOKER_TEST_CONNECTION_STRING");
        if (Environment.GetEnvironmentVariable("TCGLOOKER_TEST_USE_USER_SECRETS") == "true")
            connectionString = new ConfigurationBuilder()
                .AddUserSecrets("e06c7eaf-e101-4cdd-8d31-80e9bc229b66")
                .Build().GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Skip("Configure TCGLOOKER_TEST_CONNECTION_STRING to test an initialized PostgreSQL database. All fixtures are rolled back.");
            return;
        }

        var factory = new PostgresConnectionFactory(new PostgresOptions { ConnectionString = connectionString });
        var stores = new PostgresStoreCatalogRepository(factory);
        var search = new PostgresCardSearchRepository(factory);
        var wishlist = new PostgresWishlistRepository(factory);
        var ct = TestContext.Current.CancellationToken;
        var user = Guid.NewGuid();
        var otherUser = Guid.NewGuid();
        var global = Guid.NewGuid();
        var mine = Guid.NewGuid();
        var theirs = Guid.NewGuid();
        var disabled = Guid.NewGuid();
        var game = Guid.NewGuid();
        var card = Guid.NewGuid();
        var printing = Guid.NewGuid();
        var name = "integration" + card.ToString("N");

        // No Complete(): every fixture and repository mutation is rolled back,
        // including when an assertion fails. Connections are used sequentially.
        using var scope = new TransactionScope(TransactionScopeOption.RequiresNew,
            new TransactionOptions { IsolationLevel = IsolationLevel.ReadCommitted, Timeout = TimeSpan.FromMinutes(2) },
            TransactionScopeAsyncFlowOption.Enabled);

        await ExecuteAsync("""
            insert into tcglooker.app_user (id, external_auth_id) values (@user, @subject), (@other, @other_subject);
            insert into tcglooker.store (id, slug, name, base_url, connector_key, scope, owner_user_id, is_enabled)
            values (@global, @global::text, 'Test global', 'https://example.test/' || @global, @global::text, 'global', null, true),
                   (@mine, @mine::text, 'Test mine', 'https://example.test/' || @mine, @mine::text, 'user', @user, true),
                   (@theirs, @theirs::text, 'Test theirs', 'https://example.test/' || @theirs, @theirs::text, 'user', @other, true),
                   (@disabled, @disabled::text, 'Test disabled', 'https://example.test/' || @disabled, @disabled::text, 'global', null, false);
            insert into tcglooker.game (id, slug, name) values (@game, @game::text, 'Integration test');
            insert into tcglooker.card (id, game_id, canonical_name, normalized_name) values (@card, @game, @name, @name);
            insert into tcglooker.card_printing (id, card_id, language, finish) values (@printing, @card, 'pt-BR', 'normal');
            insert into tcglooker.listing
                (id, store_id, external_id, card_printing_id, title, normalized_title, condition,
                 price_amount, currency, quantity, url, fingerprint, availability, first_seen_at, last_seen_at)
            select gen_random_uuid(), id, @card::text, @printing, @name, @name, 'near_mint',
                   10, 'BRL', 1, 'https://example.test/offer', @card::text, 'in_stock', now(), now()
            from tcglooker.store where id in (@global, @mine, @theirs, @disabled);
            """,
            ("user", user), ("subject", user.ToString()), ("other", otherUser), ("other_subject", otherUser.ToString()),
            ("global", global), ("mine", mine), ("theirs", theirs), ("disabled", disabled),
            ("game", game), ("card", card), ("printing", printing), ("name", name));

        var anonymous = await stores.ListVisibleAsync(null, ct);
        Assert.Contains(anonymous, s => s.Id == global && s.IsSelected);
        Assert.DoesNotContain(anonymous, s => s.Id == mine || s.Id == theirs);
        var own = await stores.ListVisibleAsync(user, ct);
        Assert.Contains(own, s => s.Id == mine);
        Assert.DoesNotContain(own, s => s.Id == theirs);
        Assert.False(await stores.SetSelectionAsync(user, theirs, false, ct));
        Assert.False(await stores.SetSelectionAsync(user, Guid.NewGuid(), false, ct));

        Assert.Single((await search.SearchAsync(name, 1, 50, null, ct)).Items.Single(c => c.CardId == card).Offers);
        Assert.Equal(2, (await search.SearchAsync(name, 1, 50, user.ToString(), ct)).Items.Single(c => c.CardId == card).Offers.Count);
        Assert.True(await stores.SetSelectionAsync(user, global, false, ct));
        Assert.True(await stores.SetSelectionAsync(user, global, false, ct));
        Assert.Contains(await stores.ListVisibleAsync(user, ct), s => s.Id == global && !s.IsSelected && s.IsEnabled);
        Assert.Contains(await stores.ListVisibleAsync(otherUser, ct), s => s.Id == global && s.IsSelected);
        Assert.Single((await search.SearchAsync(name, 1, 50, user.ToString(), ct)).Items.Single(c => c.CardId == card).Offers);
        Assert.Equal(2, (await search.SearchAsync(name, 1, 50, otherUser.ToString(), ct)).Items.Single(c => c.CardId == card).Offers.Count);
        Assert.True(await stores.SetSelectionAsync(user, mine, false, ct));
        var hidden = await search.SearchAsync(name, 1, 50, user.ToString(), ct);
        Assert.DoesNotContain(hidden.Items, c => c.CardId == card);
        Assert.Equal(0, hidden.Total);
        Assert.True(await stores.SetSelectionAsync(user, global, true, ct));
        Assert.Single((await search.SearchAsync(name, 1, 50, user.ToString(), ct)).Items.Single(c => c.CardId == card).Offers);

        var item = Guid.NewGuid();
        var otherItem = Guid.NewGuid();
        // Seed directly because CreateAsync owns a separate explicit transaction.
        await ExecuteAsync("""
            insert into tcglooker.wishlist_item (id, user_id, card_id)
            values (@item, @user, @card), (@other_item, @other, @card);
            """, ("item", item), ("user", user), ("card", card), ("other_item", otherItem), ("other", otherUser));
        Assert.Contains(await wishlist.ListAsync(user, false, ct), w => w.Id == item);
        Assert.DoesNotContain(await wishlist.ListAsync(user, false, ct), w => w.Id == otherItem);
        Assert.False(await wishlist.DeleteAsync(otherUser, item, ct));

        var channel = Guid.NewGuid();
        await ExecuteAsync("""
            insert into tcglooker.notification_channel (id, user_id, type, destination_ciphertext)
            values (@channel, @user, 'telegram', decode('00', 'hex'));
            insert into tcglooker.notification_delivery
                (id, wishlist_item_id, listing_id, channel_id, event_type, availability_version, status)
            select gen_random_uuid(), @wishlist, id, @channel, 'wishlist_matched', 1, 'pending'
            from tcglooker.listing where store_id = @store;
            """, ("channel", channel), ("user", user), ("wishlist", item), ("store", global));

        Assert.True(await wishlist.DeleteAsync(user, item, ct));
        Assert.False(await wishlist.DeleteAsync(user, item, ct));
        Assert.DoesNotContain(await wishlist.ListAsync(user, true, ct), w => w.Id == item);
        Assert.Contains(await wishlist.ListAsync(otherUser, false, ct), w => w.Id == otherItem);
        Assert.Equal(0L, await ScalarAsync("select count(*) from tcglooker.notification_delivery where wishlist_item_id = @id", item));
        Assert.Equal(1L, await ScalarAsync("select count(*) from tcglooker.card where id = @id", card));
        Assert.Equal(1L, await ScalarAsync("select count(*) from tcglooker.card_printing where id = @id", printing));
        Assert.Equal(4L, await ScalarAsync("select count(*) from tcglooker.listing where card_printing_id = @id", printing));
        await ExecuteAsync("insert into tcglooker.wishlist_item (id, user_id, card_id) values (@item, @user, @card)",
            ("item", Guid.NewGuid()), ("user", user), ("card", card));
        Assert.Single(await wishlist.ListAsync(user, false, ct));

        async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = await factory.OpenConnectionAsync(ct);
            await using var command = new NpgsqlCommand(sql, connection);
            foreach (var (key, value) in parameters)
                command.Parameters.AddWithValue(key, value);
            await command.ExecuteNonQueryAsync(ct);
        }

        async Task<long> ScalarAsync(string sql, Guid id)
        {
            await using var connection = await factory.OpenConnectionAsync(ct);
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("id", id);
            return (long)(await command.ExecuteScalarAsync(ct))!;
        }
    }
}
