using TCGLooker.Domain.Marketplace;

namespace TCGLooker.Domain.Catalog;

public sealed class CardPrinting
{
    public required Guid Id { get; init; }
    public required Guid CardId { get; init; }
    public Guid? SetId { get; init; }
    public string? CollectorNumber { get; init; }
    public required string Language { get; init; }
    public CardFinish Finish { get; init; }
    public string? Variant { get; init; }
}
