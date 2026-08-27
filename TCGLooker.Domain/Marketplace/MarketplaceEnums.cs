namespace TCGLooker.Domain.Marketplace;

public enum CardCondition
{
    Unknown,
    Mint,
    NearMint,
    LightlyPlayed,
    ModeratelyPlayed,
    HeavilyPlayed,
    Damaged
}

public enum CardFinish
{
    Unknown,
    Normal,
    Holo,
    ReverseHolo
}

public enum ListingAvailability
{
    Unknown,
    InStock,
    OutOfStock
}
