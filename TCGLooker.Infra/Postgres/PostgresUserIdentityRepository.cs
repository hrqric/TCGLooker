using Npgsql;
using TCGLooker.Application.Identity;

namespace TCGLooker.Infra.Postgres;

internal sealed class PostgresUserIdentityRepository(PostgresConnectionFactory connectionFactory)
    : IUserIdentityRepository
{
    public async Task<AppUserIdentity> GetOrCreateAsync(
        string externalAuthId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            insert into tcglooker.app_user (id, external_auth_id, status, created_at)
            values (@id, @external_auth_id, 'active', now())
            on conflict (external_auth_id) do update
            set external_auth_id = excluded.external_auth_id
            returning id, external_auth_id, status
            """, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("external_auth_id", externalAuthId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Não foi possível resolver a identidade do usuário.");

        return new AppUserIdentity(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2).Equals("active", StringComparison.OrdinalIgnoreCase));
    }
}

