using System.Text.Json;
using Npgsql;
using TCGLooker.Application.Ingestion;

namespace TCGLooker.Infra.Postgres;

internal sealed partial class PostgresScrapeRepository
{
    public async Task<ProductRefreshTarget?> FindDueAsync(
        Guid storeId,
        DateTimeOffset staleBefore,
        DateTimeOffset retryBefore,
        bool preferWishlist,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            with products as (
                select l.url, max(l.last_seen_at) as last_observed_at,
                       bool_or(exists (
                           select 1
                           from tcglooker.card_printing p
                           join tcglooker.wishlist_item w on w.card_id = p.card_id
                           join tcglooker.app_user u on u.id = w.user_id and u.status = 'active'
                           where p.id = l.card_printing_id and w.is_active
                             and (w.card_printing_id is null or w.card_printing_id = p.id)
                             and (s.scope = 'global' or s.owner_user_id = w.user_id)
                             and not exists (
                                 select 1 from tcglooker.user_store us
                                 where us.user_id = w.user_id and us.store_id = s.id and not us.is_enabled)
                       )) as is_wishlisted
                from tcglooker.listing l
                join tcglooker.store s on s.id = l.store_id and s.is_enabled
                where l.store_id = @store_id
                group by l.url
                having max(l.last_seen_at) <= @stale_before
            ), recent_attempts as materialized (
                select cursor::jsonb ->> 'ProductUrl' as url
                from tcglooker.scrape_run
                where store_id = @store_id and mode = 'incremental'
                  and started_at > @retry_before
                  and cursor like '{"Kind":"product_refresh",%'
            )
            select url, last_observed_at, is_wishlisted
            from products p
            where not exists (select 1 from recent_attempts a where a.url = p.url)
            order by case when @prefer_wishlist then is_wishlisted else false end desc,
                     last_observed_at, url
            limit 1
            """, connection);
        command.Parameters.AddWithValue("store_id", storeId);
        command.Parameters.AddWithValue("stale_before", staleBefore);
        command.Parameters.AddWithValue("retry_before", retryBefore);
        command.Parameters.AddWithValue("prefer_wishlist", preferWishlist);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ProductRefreshTarget(new Uri(reader.GetString(0)), reader.GetFieldValue<DateTimeOffset>(1), reader.GetBoolean(2))
            : null;
    }

    public async Task<ScrapeExecution> StartProductAsync(
        Guid storeId,
        Uri productUri,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken = default)
    {
        var runId = Guid.NewGuid();
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var abandoned = new NpgsqlCommand("""
            update tcglooker.scrape_run
            set status = 'failed', finished_at = @started_at, error_code = 'WorkerInterrupted'
            where store_id = @store_id and status = 'running'
            """, connection, transaction))
        {
            abandoned.Parameters.AddWithValue("store_id", storeId);
            abandoned.Parameters.AddWithValue("started_at", startedAt);
            await abandoned.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var command = new NpgsqlCommand("""
            insert into tcglooker.scrape_run (id, store_id, started_at, status, mode, cursor)
            select @run_id, id, @started_at, 'running', 'incremental', @cursor
            from tcglooker.store where id = @store_id and is_enabled
            returning id
            """, connection, transaction);
        command.Parameters.AddWithValue("run_id", runId);
        command.Parameters.AddWithValue("store_id", storeId);
        command.Parameters.AddWithValue("started_at", startedAt);
        command.Parameters.AddWithValue("cursor", JsonSerializer.Serialize(
            new ProductRefreshAudit("product_refresh", productUri.AbsoluteUri)));
        if (await command.ExecuteScalarAsync(cancellationToken) is not Guid)
            throw new InvalidOperationException($"Enabled store '{storeId}' was not found.");
        await transaction.CommitAsync(cancellationToken);
        return new ScrapeExecution(runId, storeId, ScrapeMode.Incremental,
            new ScrapeProgress(runId, startedAt, NextPage: null));
    }

    public async Task SaveProductAsync(
        ScrapeExecution execution,
        IReadOnlyCollection<ExternalListing> listings,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var changed = await UpsertListingsAsync(connection, transaction, execution, listings, observedAt, cancellationToken);
        await using var command = new NpgsqlCommand("""
            update tcglooker.scrape_run
            set status = 'succeeded', finished_at = @observed_at,
                items_seen = @items_seen, items_changed = @items_changed
            where id = @run_id and status = 'running'
              and mode = 'incremental' and cursor like '{"Kind":"product_refresh",%'
            """, connection, transaction);
        command.Parameters.AddWithValue("run_id", execution.RunId);
        command.Parameters.AddWithValue("observed_at", observedAt);
        command.Parameters.AddWithValue("items_seen", listings.Count);
        command.Parameters.AddWithValue("items_changed", changed);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("The product refresh attempt is no longer running.");
        await transaction.CommitAsync(cancellationToken);
    }

    private sealed record ProductRefreshAudit(string Kind, string ProductUrl);
}
