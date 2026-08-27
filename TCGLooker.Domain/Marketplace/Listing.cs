using TCGLooker.Domain.Common;

namespace TCGLooker.Domain.Marketplace;

public sealed class Listing
{
    public required Guid Id { get; init; }
    public required Guid StoreId { get; init; }
    public required string ExternalId { get; init; }
    public Guid? CardPrintingId { get; private set; }
    public required string Title { get; init; }
    public required string NormalizedTitle { get; init; }
    public required Uri Url { get; init; }
    public required string Fingerprint { get; init; }
    public CardCondition Condition { get; private set; }
    public Money Price { get; private set; }
    public int? Quantity { get; private set; }
    public ListingAvailability Availability { get; private set; }
    public long AvailabilityVersion { get; private set; }
    public required DateTimeOffset FirstSeenAt { get; init; }
    public DateTimeOffset LastSeenAt { get; private set; }

    public void IdentifyAs(Guid cardPrintingId) => CardPrintingId = cardPrintingId;

    public void Observe(
        Money price,
        CardCondition condition,
        int? quantity,
        ListingAvailability availability,
        DateTimeOffset observedAt)
    {
        if (quantity is < 0)
            throw new ArgumentOutOfRangeException(nameof(quantity));

        if (observedAt < LastSeenAt)
            throw new ArgumentOutOfRangeException(nameof(observedAt), "Observations must be chronological.");

        if (AvailabilityVersion == 0 || Availability != availability)
            AvailabilityVersion++;

        Price = price;
        Condition = condition;
        Quantity = quantity;
        Availability = availability;
        LastSeenAt = observedAt;
    }
}
