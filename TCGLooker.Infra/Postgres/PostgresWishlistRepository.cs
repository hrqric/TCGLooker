using Npgsql;
using NpgsqlTypes;
using TCGLooker.Application.Watchlist;
using TCGLooker.Domain.Marketplace;

namespace TCGLooker.Infra.Postgres;

internal sealed class PostgresWishlistRepository(PostgresConnectionFactory connectionFactory)
    : IWishlistRepository
{
    public async Task<WishlistView> CreateAsync(
        Guid userId,
        CreateWishlistItem item,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var cardName = await ValidateTargetAsync(connection, transaction, item, cancellationToken);
        try
        {
            await using var insert = new NpgsqlCommand("""
                insert into tcglooker.wishlist_item
                    (id, user_id, card_id, card_printing_id, max_price_amount,
                     max_price_currency, minimum_condition, is_active, created_at)
                values
                    (@id, @user_id, @card_id, @printing_id, @max_price,
                     @currency, @minimum_condition, true, now())
                returning created_at
                """, connection, transaction);
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("user_id", userId);
            insert.Parameters.AddWithValue("card_id", item.CardId);
            insert.Parameters.Add("printing_id", NpgsqlDbType.Uuid).Value =
                (object?)item.CardPrintingId ?? DBNull.Value;
            insert.Parameters.Add("max_price", NpgsqlDbType.Numeric).Value =
                (object?)item.MaximumPriceAmount ?? DBNull.Value;
            insert.Parameters.Add("currency", NpgsqlDbType.Char).Value =
                (object?)item.MaximumPriceCurrency?.ToUpperInvariant() ?? DBNull.Value;
            insert.Parameters.Add("minimum_condition", NpgsqlDbType.Text).Value =
                item.MinimumCondition is null ? DBNull.Value : ToDatabase(item.MinimumCondition.Value);

            var createdAt = (DateTime)(await insert.ExecuteScalarAsync(cancellationToken))!;
            await CreateEventsForExistingMatchesAsync(
                connection, transaction, id, userId, item, cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new WishlistView(
                id,
                item.CardId,
                cardName,
                item.CardPrintingId,
                item.MaximumPriceAmount,
                item.MaximumPriceCurrency?.ToUpperInvariant(),
                item.MinimumCondition,
                true,
                new DateTimeOffset(DateTime.SpecifyKind(createdAt, DateTimeKind.Utc)));
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new WishlistConflictException("Já existe uma wishlist ativa para essa carta e impressão.");
        }
    }

    public async Task<IReadOnlyCollection<WishlistView>> ListAsync(
        Guid userId,
        bool includeInactive,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            select w.id, w.card_id, c.canonical_name, w.card_printing_id,
                   w.max_price_amount, w.max_price_currency, w.minimum_condition,
                   w.is_active, w.created_at
            from tcglooker.wishlist_item w
            join tcglooker.card c on c.id = w.card_id
            where w.user_id = @user_id
              and (@include_inactive or w.is_active)
            order by w.is_active desc, w.created_at desc, w.id
            """, connection);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("include_inactive", includeInactive);

        var result = new List<WishlistView>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(Read(reader));
        return result;
    }

    public async Task<bool> DeleteAsync(
        Guid userId,
        Guid wishlistItemId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            delete from tcglooker.wishlist_item
            where id = @id and user_id = @user_id
            """, connection);
        command.Parameters.AddWithValue("id", wishlistItemId);
        command.Parameters.AddWithValue("user_id", userId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task<string> ValidateTargetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CreateWishlistItem item,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            select c.canonical_name
            from tcglooker.card c
            where c.id = @card_id
              and (@printing_id is null or exists (
                  select 1 from tcglooker.card_printing p
                  where p.id = @printing_id and p.card_id = c.id))
            """, connection, transaction);
        command.Parameters.AddWithValue("card_id", item.CardId);
        command.Parameters.Add("printing_id", NpgsqlDbType.Uuid).Value =
            (object?)item.CardPrintingId ?? DBNull.Value;
        var cardName = await command.ExecuteScalarAsync(cancellationToken);
        if (cardName is not string value)
            throw new WishlistTargetNotFoundException("A carta ou impressão informada não existe.");
        return value;
    }

    private static async Task CreateEventsForExistingMatchesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid wishlistId,
        Guid userId,
        CreateWishlistItem item,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            insert into tcglooker.outbox_message
                (id, type, payload, occurred_at, idempotency_key)
            select gen_random_uuid(),
                   'wishlist.matched',
                   jsonb_build_object(
                       'wishlistId', @wishlist_id,
                       'listingId', l.id,
                       'availabilityVersion', l.availability_version,
                       'storeId', l.store_id,
                       'priceAmount', l.price_amount,
                       'currency', l.currency,
                       'url', l.url),
                   now(),
                   concat('wishlist:', @wishlist_id, ':listing:', l.id,
                          ':availability:', l.availability_version)
            from tcglooker.listing l
            join tcglooker.card_printing p on p.id = l.card_printing_id
            join tcglooker.store s on s.id = l.store_id
            where l.availability = 'in_stock'
              and p.card_id = @card_id
              and (@printing_id is null or p.id = @printing_id)
              and (@max_price is null
                   or (l.currency = @currency and l.price_amount <= @max_price))
              and tcglooker.condition_rank(l.condition)
                  >= tcglooker.condition_rank(@minimum_condition)
              and (s.scope = 'global' or s.owner_user_id = @user_id)
            on conflict (idempotency_key) do nothing
            """, connection, transaction);
        command.Parameters.AddWithValue("wishlist_id", wishlistId);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("card_id", item.CardId);
        command.Parameters.Add("printing_id", NpgsqlDbType.Uuid).Value =
            (object?)item.CardPrintingId ?? DBNull.Value;
        command.Parameters.Add("max_price", NpgsqlDbType.Numeric).Value =
            (object?)item.MaximumPriceAmount ?? DBNull.Value;
        command.Parameters.Add("currency", NpgsqlDbType.Char).Value =
            (object?)item.MaximumPriceCurrency?.ToUpperInvariant() ?? DBNull.Value;
        command.Parameters.Add("minimum_condition", NpgsqlDbType.Text).Value =
            item.MinimumCondition is null ? DBNull.Value : ToDatabase(item.MinimumCondition.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static WishlistView Read(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetGuid(1),
        reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetGuid(3),
        reader.IsDBNull(4) ? null : reader.GetDecimal(4),
        reader.IsDBNull(5) ? null : reader.GetString(5).Trim(),
        reader.IsDBNull(6) ? null : FromDatabase(reader.GetString(6)),
        reader.GetBoolean(7),
        reader.GetFieldValue<DateTimeOffset>(8));

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

    private static CardCondition FromDatabase(string value) => value switch
    {
        "mint" => CardCondition.Mint,
        "near_mint" => CardCondition.NearMint,
        "lightly_played" => CardCondition.LightlyPlayed,
        "moderately_played" => CardCondition.ModeratelyPlayed,
        "heavily_played" => CardCondition.HeavilyPlayed,
        "damaged" => CardCondition.Damaged,
        _ => CardCondition.Unknown
    };
}
