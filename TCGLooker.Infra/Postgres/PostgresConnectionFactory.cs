using Npgsql;

namespace TCGLooker.Infra.Postgres;

public sealed class PostgresConnectionFactory(PostgresOptions options)
{
    public async Task<NpgsqlConnection> OpenConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new InvalidOperationException(
                "PostgreSQL connection string is missing. Set ConnectionStrings__DefaultConnection.");
        }

        var connection = new NpgsqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
