using Npgsql;
using TCGLooker.Application.Notifications;

namespace TCGLooker.Infra.Postgres;

internal sealed class PostgresNotificationOutboxProcessor(PostgresConnectionFactory connectionFactory)
    : INotificationOutboxProcessor
{
    public async Task<NotificationOutboxBatch> ProcessAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            with claimed as materialized (
                select id,
                       (payload ->> 'wishlistId')::uuid as wishlist_id,
                       (payload ->> 'listingId')::uuid as listing_id,
                       (payload ->> 'availabilityVersion')::bigint as availability_version
                from tcglooker.outbox_message
                where processed_at is null
                  and type = 'wishlist.matched'
                  and coalesce(next_attempt_at, occurred_at) <= now()
                order by occurred_at, id
                limit @batch_size
                for update skip locked
            ), inserted as (
                insert into tcglooker.notification_delivery
                    (id, wishlist_item_id, listing_id, channel_id, event_type,
                     availability_version, status, attempts, next_attempt_at, created_at)
                select gen_random_uuid(), c.wishlist_id, c.listing_id, channel.id,
                       'wishlist_matched', c.availability_version,
                       'pending', 0, now(), now()
                from claimed c
                join tcglooker.wishlist_item w
                  on w.id = c.wishlist_id and w.is_active
                join tcglooker.notification_channel channel
                  on channel.user_id = w.user_id
                 and channel.is_enabled
                 and channel.verified_at is not null
                on conflict
                    (wishlist_item_id, listing_id, channel_id, event_type, availability_version)
                do nothing
                returning 1
            ), processed as (
                update tcglooker.outbox_message message
                set processed_at = now(), error_code = null
                from claimed
                where message.id = claimed.id
                returning 1
            )
            select (select count(*) from processed),
                   (select count(*) from inserted)
            """, connection, transaction);
        command.Parameters.AddWithValue("batch_size", Math.Clamp(batchSize, 1, 1000));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var result = new NotificationOutboxBatch(reader.GetInt32(0), reader.GetInt32(1));
        await reader.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}

