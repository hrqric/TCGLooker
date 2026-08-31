using Microsoft.Extensions.Logging.Abstractions;
using TCGLooker.Application.Ingestion;
using TCGLooker.Domain.Common;
using TCGLooker.Domain.Marketplace;
using TCGLooker.Infra.Ingestion;
using Xunit;

namespace TCGLooker.Infra.Tests.Ingestion;

public sealed class ScrapeOrchestratorTests
{
    [Fact]
    public async Task Failed_full_crawl_publishes_available_offers_but_does_not_mark_unavailable()
    {
        var available = Listing("available", 2);
        var unavailable = Listing("unavailable", 0);
        var connector = new FakeConnector(
            new ScrapePage([available, unavailable], 2),
            new HttpRequestException("page failed"));
        var repository = new FakeRepository();
        var orchestrator = new ScrapeOrchestrator(
            repository,
            TimeProvider.System,
            NullLogger<ScrapeOrchestrator>.Instance);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            orchestrator.RunAsync(connector, ScrapeMode.Full, TestContext.Current.CancellationToken));

        Assert.Equal(["available"], repository.PublishedExternalIds);
        Assert.False(repository.Completed);
        Assert.True(repository.Failed);
    }

    [Fact]
    public async Task Successful_crawl_defers_unavailable_offers_until_completion()
    {
        var connector = new FakeConnector(
            new ScrapePage([Listing("available", 1), Listing("unavailable", 0)], null));
        var repository = new FakeRepository();
        var orchestrator = new ScrapeOrchestrator(
            repository,
            TimeProvider.System,
            NullLogger<ScrapeOrchestrator>.Instance);

        await orchestrator.RunAsync(connector, ScrapeMode.Full, TestContext.Current.CancellationToken);

        Assert.Equal(["available"], repository.PublishedExternalIds);
        Assert.Equal(["unavailable"], repository.CompletedUnavailableExternalIds);
        Assert.True(repository.Completed);
        Assert.False(repository.Failed);
    }

    private static ExternalListing Listing(string externalId, int quantity) => new(
        externalId,
        "Charizard",
        "base",
        "Base Set",
        "4",
        "pt-BR",
        CardFinish.Holo,
        null,
        "Charizard (#4)",
        new Uri($"https://example.test/{externalId}"),
        new Money(100, "BRL"),
        CardCondition.NearMint,
        quantity,
        new Dictionary<string, string>());

    private sealed class FakeConnector(params object[] pages) : IStoreConnector
    {
        private int _index;
        public string Key => "fake";

        public Task<ScrapePage> FetchAsync(
            ScrapeRequest request,
            CancellationToken cancellationToken = default)
        {
            var result = pages[_index++];
            return result switch
            {
                ScrapePage page => Task.FromResult(page),
                Exception exception => Task.FromException<ScrapePage>(exception),
                _ => throw new InvalidOperationException()
            };
        }
    }

    private sealed class FakeRepository : IScrapeRepository
    {
        private readonly ScrapeExecution _execution = new(Guid.NewGuid(), Guid.NewGuid(), ScrapeMode.Full);

        public List<string> PublishedExternalIds { get; } = [];
        public List<string> CompletedUnavailableExternalIds { get; } = [];
        public bool Completed { get; private set; }
        public bool Failed { get; private set; }

        public Task<ScrapeExecution> StartAsync(
            string storeKey,
            ScrapeMode mode,
            CancellationToken cancellationToken = default) => Task.FromResult(_execution);

        public Task<int> UpsertAvailableAsync(
            ScrapeExecution execution,
            IReadOnlyCollection<ExternalListing> listings,
            DateTimeOffset observedAt,
            CancellationToken cancellationToken = default)
        {
            PublishedExternalIds.AddRange(listings.Select(listing => listing.ExternalId));
            return Task.FromResult(listings.Count);
        }

        public Task<int> CompleteAsync(
            ScrapeExecution execution,
            IReadOnlyCollection<ExternalListing> unavailableListings,
            int itemsSeen,
            int itemsChanged,
            DateTimeOffset observedAt,
            DateTimeOffset finishedAt,
            CancellationToken cancellationToken = default)
        {
            Completed = true;
            CompletedUnavailableExternalIds.AddRange(
                unavailableListings.Select(listing => listing.ExternalId));
            return Task.FromResult(unavailableListings.Count);
        }

        public Task FailAsync(
            ScrapeExecution execution,
            string errorCode,
            DateTimeOffset finishedAt,
            CancellationToken cancellationToken = default)
        {
            Failed = true;
            return Task.CompletedTask;
        }

        public Task<int> PurgeUnavailableAsync(
            DateTimeOffset olderThan,
            CancellationToken cancellationToken = default) => Task.FromResult(0);
    }
}
