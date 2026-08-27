namespace TCGLooker.Domain.Catalog;

public sealed class Card
{
    public required Guid Id { get; init; }
    public required Guid GameId { get; init; }
    public required string CanonicalName { get; init; }
    public required string NormalizedName { get; init; }
}
