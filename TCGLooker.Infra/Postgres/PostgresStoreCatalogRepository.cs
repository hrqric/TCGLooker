using Npgsql;
using NpgsqlTypes;
using TCGLooker.Application.Stores;

namespace TCGLooker.Infra.Postgres;

internal sealed class PostgresStoreCatalogRepository(PostgresConnectionFactory connectionFactory)
    : IStoreCatalogRepository
{
    public async Task<IReadOnlyCollection<StoreView>> ListVisibleAsync(
        Guid? userId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            select s.id, s.slug, s.name, s.base_url, s.connector_type, s.scope,
                   s.is_enabled, coalesce(selection.is_enabled, true)
            from tcglooker.store s
            left join tcglooker.user_store selection
              on selection.store_id = s.id and selection.user_id = @user_id
            where s.scope = 'global' or s.owner_user_id = @user_id
            order by s.name, s.id
            """, connection);
        command.Parameters.Add("user_id", NpgsqlDbType.Uuid).Value = (object?)userId ?? DBNull.Value;

        var result = new List<StoreView>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new StoreView(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                new Uri(reader.GetString(3)), reader.GetString(4), FromDatabase(reader.GetString(5)),
                reader.GetBoolean(6), reader.GetBoolean(7)));
        return result;
    }

    public async Task<bool> SetSelectionAsync(
        Guid userId,
        Guid storeId,
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            insert into tcglooker.user_store (user_id, store_id, is_enabled)
            select @user_id, s.id, @is_enabled
            from tcglooker.store s
            where s.id = @store_id
              and (s.scope = 'global' or s.owner_user_id = @user_id)
            on conflict (user_id, store_id)
            do update set is_enabled = excluded.is_enabled
            """, connection);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("store_id", storeId);
        command.Parameters.AddWithValue("is_enabled", isEnabled);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<IReadOnlyCollection<StoreSource>> ListEnabledAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            select id, slug, name, base_url, connector_key, connector_type, scope, owner_user_id
            from tcglooker.store
            where is_enabled
            order by id
            """, connection);

        var stores = new List<StoreSource>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            stores.Add(new StoreSource(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                new Uri(reader.GetString(3)),
                reader.GetString(4),
                reader.GetString(5),
                FromDatabase(reader.GetString(6)),
                reader.IsDBNull(7) ? null : reader.GetGuid(7)));
        }

        return stores;
    }

    public async Task<StoreRegistrationResult> CreateAsync(
        StoreRegistration registration,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var appUserId = Guid.NewGuid();
            await using (var userCommand = new NpgsqlCommand("""
                insert into tcglooker.app_user (id, external_auth_id)
                values (@id, @external_auth_id)
                on conflict (external_auth_id) do update set external_auth_id = excluded.external_auth_id
                returning id
                """, connection, transaction))
            {
                userCommand.Parameters.AddWithValue("id", appUserId);
                userCommand.Parameters.AddWithValue("external_auth_id", registration.ExternalUserId);
                appUserId = (Guid)(await userCommand.ExecuteScalarAsync(cancellationToken))!;
            }

            var storeId = Guid.NewGuid();
            await using (var storeCommand = new NpgsqlCommand("""
                insert into tcglooker.store
                    (id, slug, name, base_url, connector_key, connector_type,
                     scope, owner_user_id, created_by_user_id, is_enabled)
                values
                    (@id, @slug, @name, @base_url, @connector_key, @connector_type,
                     @scope, @owner_user_id, @created_by_user_id, true)
                """, connection, transaction))
            {
                storeCommand.Parameters.AddWithValue("id", storeId);
                storeCommand.Parameters.AddWithValue("slug", registration.Slug);
                storeCommand.Parameters.AddWithValue("name", registration.Name);
                storeCommand.Parameters.AddWithValue("base_url", registration.BaseUrl.AbsoluteUri);
                storeCommand.Parameters.AddWithValue("connector_key", registration.Slug);
                storeCommand.Parameters.AddWithValue("connector_type", registration.ConnectorType);
                storeCommand.Parameters.AddWithValue("scope", ToDatabase(registration.Scope));
                storeCommand.Parameters.AddWithValue(
                    "owner_user_id",
                    registration.Scope == StoreScope.User ? appUserId : DBNull.Value);
                storeCommand.Parameters.AddWithValue("created_by_user_id", appUserId);
                await storeCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            if (registration.Scope == StoreScope.User)
            {
                await using var selectionCommand = new NpgsqlCommand("""
                    insert into tcglooker.user_store (user_id, store_id, is_enabled)
                    values (@user_id, @store_id, true)
                    on conflict (user_id, store_id) do update set is_enabled = true
                    """, connection, transaction);
                selectionCommand.Parameters.AddWithValue("user_id", appUserId);
                selectionCommand.Parameters.AddWithValue("store_id", storeId);
                await selectionCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return new StoreRegistrationResult(
                storeId,
                registration.Slug,
                registration.Name,
                registration.BaseUrl,
                registration.ConnectorType,
                registration.Scope);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw new StoreConflictException("Já existe um site com o mesmo slug ou endereço.");
        }
    }

    private static StoreScope FromDatabase(string value) => value switch
    {
        "global" => StoreScope.Global,
        "user" => StoreScope.User,
        _ => throw new InvalidDataException($"Unknown store scope '{value}'.")
    };

    private static string ToDatabase(StoreScope scope) => scope switch
    {
        StoreScope.Global => "global",
        StoreScope.User => "user",
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null)
    };
}
