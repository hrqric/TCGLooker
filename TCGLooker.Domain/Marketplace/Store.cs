namespace TCGLooker.Domain.Marketplace;

public sealed class Store
{
    public required Guid Id { get; init; }
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public required Uri BaseUrl { get; init; }
    public required string ConnectorKey { get; init; }
    public bool IsEnabled { get; private set; } = true;

    public void Disable() => IsEnabled = false;
    public void Enable() => IsEnabled = true;
}
