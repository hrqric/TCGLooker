using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using TCGLooker.Application.Ingestion;
using TCGLooker.Domain.Marketplace;
using TCGLooker.Infra.Connectors;

namespace TCGLooker.Infra.Postgres;

internal sealed class PostgresScrapeRepository(PostgresConnectionFactory connectionFactory) : IScrapeRepository
{
    public async Task<ScrapeExecution> StartAsync(
        string storeKey,
        ScrapeMode mode,
        CancellationToken cancellationToken = default)
    {
        var runId = Guid.NewGuid();
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            insert into tcglooker.scrape_run (id, store_id, started_at, status, mode)
            select @run_id, id, now(), 'running', @mode
            from tcglooker.store
            where connector_key = @store_key and is_enabled
            returning store_id
            """, connection);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("store_key", storeKey);
        command.Parameters.AddWithValue("mode", ToDatabase(mode));

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is not Guid storeId)
            throw new InvalidOperationException($"Enabled store '{storeKey}' was not found.");
        return new ScrapeExecution(runId, storeId, mode);
    }

    public async Task<int> UpsertAvailableAsync(
        ScrapeExecution execution,
        IReadOnlyCollection<ExternalListing> listings,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default)
    {
        if (listings.Count == 0)
            return 0;

        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var changed = await UpsertListingsAsync(
            connection, transaction, execution, listings, observedAt, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return changed;
    }

    public async Task<int> CompleteAsync(
        ScrapeExecution execution,
        IReadOnlyCollection<ExternalListing> unavailableListings,
        int itemsSeen,
        int itemsChanged,
        DateTimeOffset observedAt,
        DateTimeOffset finishedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var unavailableChanged = await UpsertListingsAsync(
            connection,
            transaction,
            execution,
            unavailableListings,
            observedAt,
            cancellationToken);

        if (execution.Mode == ScrapeMode.Full)
        {
            await using var reconcile = new NpgsqlCommand("""
                update tcglooker.listing
                set consecutive_misses = consecutive_misses + 1,
                    availability = case
                        when consecutive_misses + 1 >= 2 then 'out_of_stock'
                        else availability
                    end,
                    availability_version = case
                        when consecutive_misses + 1 >= 2 and availability <> 'out_of_stock'
                            then availability_version + 1
                        else availability_version
                    end,
                    unavailable_since = case
                        when consecutive_misses + 1 >= 2
                            then coalesce(unavailable_since, @finished_at)
                        else unavailable_since
                    end
                where store_id = @store_id
                  and last_seen_run_id is distinct from @run_id
                """, connection, transaction);
            reconcile.Parameters.AddWithValue("store_id", execution.StoreId);
            reconcile.Parameters.AddWithValue("run_id", execution.RunId);
            reconcile.Parameters.AddWithValue("finished_at", finishedAt);
            await reconcile.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var complete = new NpgsqlCommand("""
            update tcglooker.scrape_run
            set finished_at = @finished_at,
                status = 'succeeded',
                items_seen = @items_seen,
                items_changed = @items_changed
            where id = @run_id and status = 'running'
            """, connection, transaction);
        complete.Parameters.AddWithValue("finished_at", finishedAt);
        complete.Parameters.AddWithValue("items_seen", itemsSeen);
        complete.Parameters.AddWithValue("items_changed", itemsChanged + unavailableChanged);
        complete.Parameters.AddWithValue("run_id", execution.RunId);
        await complete.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return unavailableChanged;
    }

    private static async Task<int> UpsertListingsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ScrapeExecution execution,
        IReadOnlyCollection<ExternalListing> listings,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        var changed = 0;
        foreach (var listing in listings)
        {
            var setId = await UpsertSetAsync(connection, transaction, listing, cancellationToken);
            var cardId = await UpsertCardAsync(connection, transaction, listing.CardName, cancellationToken);
            var printingId = await UpsertPrintingAsync(
                connection, transaction, cardId, setId, listing, cancellationToken);
            changed += await UpsertListingAsync(
                connection, transaction, execution, printingId, listing, observedAt, cancellationToken);
        }

        return changed;
    }

    public async Task FailAsync(
        ScrapeExecution execution,
        string errorCode,
        DateTimeOffset finishedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            update tcglooker.scrape_run
            set finished_at = @finished_at, status = 'failed', error_code = @error_code
            where id = @run_id and status = 'running'
            """, connection);
        command.Parameters.AddWithValue("finished_at", finishedAt);
        command.Parameters.AddWithValue("error_code", errorCode[..Math.Min(errorCode.Length, 200)]);
        command.Parameters.AddWithValue("run_id", execution.RunId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> PurgeUnavailableAsync(
        DateTimeOffset olderThan,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            delete from tcglooker.listing l
            where l.availability = 'out_of_stock'
              and l.unavailable_since < @older_than
              and not exists (
                  select 1 from tcglooker.notification_delivery d
                  where d.listing_id = l.id and d.status = 'pending')
              and not exists (
                  select 1 from tcglooker.outbox_message o
                  where o.processed_at is null
                    and o.payload ->> 'listingId' = l.id::text)
            """, connection);
        command.Parameters.AddWithValue("older_than", olderThan);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Guid> UpsertSetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ExternalListing listing,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            insert into tcglooker.card_set (id, game_id, external_code, name)
            select @id, id, @external_code, @name from tcglooker.game where slug = 'pokemon'
            on conflict (game_id, external_code) do update set name = excluded.name
            returning id
            """, connection, transaction);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("external_code", listing.SetExternalCode);
        command.Parameters.AddWithValue("name", listing.SetName);
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<Guid> UpsertCardAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string cardName,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            insert into tcglooker.card (id, game_id, canonical_name, normalized_name)
            select @id, id, @name, @normalized_name from tcglooker.game where slug = 'pokemon'
            on conflict (game_id, normalized_name) do update
            set canonical_name = excluded.canonical_name
            returning id
            """, connection, transaction);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("name", cardName);
        command.Parameters.AddWithValue("normalized_name", TextNormalizer.Normalize(cardName));
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<Guid> UpsertPrintingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid cardId,
        Guid setId,
        ExternalListing listing,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            insert into tcglooker.card_printing
                (id, card_id, set_id, collector_number, language, finish, variant)
            values (@id, @card_id, @set_id, @collector_number, @language, @finish, @variant)
            on conflict (card_id, set_id, collector_number, language, finish, variant)
            do update set language = excluded.language
            returning id
            """, connection, transaction);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("card_id", cardId);
        command.Parameters.AddWithValue("set_id", setId);
        command.Parameters.AddWithValue("collector_number", (object?)listing.CollectorNumber ?? DBNull.Value);
        command.Parameters.AddWithValue("language", listing.Language);
        command.Parameters.AddWithValue("finish", ToDatabase(listing.Finish));
        command.Parameters.AddWithValue("variant", (object?)listing.Variant ?? DBNull.Value);
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<int> UpsertListingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ScrapeExecution execution,
        Guid printingId,
        ExternalListing listing,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        var availability = listing.Quantity switch
        {
            > 0 => "in_stock",
            0 => "out_of_stock",
            _ => "unknown"
        };
        await using var command = new NpgsqlCommand("""
            insert into tcglooker.listing
                (id, store_id, external_id, card_printing_id, title, normalized_title,
                 condition, price_amount, currency, quantity, url, fingerprint,
                 availability, raw_attributes, first_seen_at, last_seen_at,
                 last_seen_run_id, consecutive_misses, unavailable_since)
            values
                (@id, @store_id, @external_id, @printing_id, @title, @normalized_title,
                 @condition, @price, @currency, @quantity, @url, @fingerprint,
                 @availability, @attributes, @observed_at, @observed_at,
                 @run_id, 0, case when @availability = 'out_of_stock' then @observed_at end)
            on conflict (store_id, external_id) do update set
                card_printing_id = excluded.card_printing_id,
                title = excluded.title,
                normalized_title = excluded.normalized_title,
                condition = excluded.condition,
                price_amount = excluded.price_amount,
                currency = excluded.currency,
                quantity = excluded.quantity,
                url = excluded.url,
                fingerprint = excluded.fingerprint,
                availability_version = case
                    when tcglooker.listing.availability <> excluded.availability
                        then tcglooker.listing.availability_version + 1
                    else tcglooker.listing.availability_version
                end,
                availability = excluded.availability,
                raw_attributes = excluded.raw_attributes,
                last_seen_at = excluded.last_seen_at,
                last_seen_run_id = excluded.last_seen_run_id,
                consecutive_misses = 0,
                unavailable_since = case
                    when excluded.availability = 'out_of_stock'
                        then coalesce(tcglooker.listing.unavailable_since, excluded.last_seen_at)
                    else null
                end
            returning 1
            """, connection, transaction);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("store_id", execution.StoreId);
        command.Parameters.AddWithValue("external_id", listing.ExternalId);
        command.Parameters.AddWithValue("printing_id", printingId);
        command.Parameters.AddWithValue("title", listing.Title);
        command.Parameters.AddWithValue("normalized_title", TextNormalizer.Normalize(listing.Title));
        command.Parameters.AddWithValue("condition", ToDatabase(listing.Condition));
        command.Parameters.AddWithValue("price", listing.Price.Amount);
        command.Parameters.AddWithValue("currency", listing.Price.Currency);
        command.Parameters.AddWithValue("quantity", (object?)listing.Quantity ?? DBNull.Value);
        command.Parameters.AddWithValue("url", listing.Url.AbsoluteUri);
        command.Parameters.AddWithValue("fingerprint", listing.ExternalId);
        command.Parameters.AddWithValue("availability", availability);
        command.Parameters.Add("attributes", NpgsqlDbType.Jsonb).Value = JsonSerializer.Serialize(listing.Attributes);
        command.Parameters.AddWithValue("observed_at", observedAt);
        command.Parameters.AddWithValue("run_id", execution.RunId);
        return (int)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static string ToDatabase(ScrapeMode value) => value == ScrapeMode.Full ? "full" : "incremental";

    private static string ToDatabase(CardFinish value) => value switch
    {
        CardFinish.Normal => "normal",
        CardFinish.Holo => "holo",
        CardFinish.ReverseHolo => "reverse_holo",
        _ => "unknown"
    };

    private static string ToDatabase(CardCondition value) => value switch
    {
        CardCondition.Mint => "mint",
        CardCondition.NearMint => "near_mint",
        CardCondition.LightlyPlayed => "lightly_played",
        CardCondition.ModeratelyPlayed => "moderately_played",
        CardCondition.HeavilyPlayed => "heavily_played",
        CardCondition.Damaged => "damaged",
        _ => "unknown"
    };
}
