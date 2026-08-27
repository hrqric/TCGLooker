namespace TCGLooker.Domain.Catalog;

public sealed class CardSet
{
    public required Guid Id { get; init; }
    public required Guid GameId { get; init; }
    public string? ExternalCode { get; init; }
    public required string Name { get; init; }
    public DateOnly? ReleasedOn { get; init; }
}
