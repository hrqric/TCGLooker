namespace TCGLooker.Domain.Catalog;

public sealed class Game
{
    public required Guid Id { get; init; }
    public required string Slug { get; init; }
    public required string Name { get; init; }
}
