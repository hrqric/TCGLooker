namespace TCGLooker.Infra.Postgres;

public sealed class PostgresOptions
{
    public const string ConnectionStringName = "DefaultConnection";

    public string? ConnectionString { get; init; }
}
