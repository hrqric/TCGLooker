using Microsoft.Extensions.Logging.Abstractions;
using TCGLooker.Application.Ingestion;
using TCGLooker.Domain.Common;
using TCGLooker.Domain.Marketplace;
using TCGLooker.Infra.Ingestion;
using Xunit;

namespace TCGLooker.Infra.Tests.Ingestion;

public sealed class ScrapeOrchestratorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Batches_resume_after_recreation_and_restart_only_after_the_last_page()
    {
        var repository = new FakeRepository();
        var nextUri = new Uri("https://example.test/?page=2&tcg=2");
        var connector = new FakeConnector(
            new ScrapePage([Listing("a")], 2, nextUri, "page1"),
            new ScrapePage([Listing("z")], null, Fingerprint: "page2"),
            new ScrapePage([Listing("a")], 2, nextUri, "page1"));

        await Create(repository).RunAsync(connector, ScrapeMode.Full, TestContext.Current.CancellationToken);
        var cycle = repository.Progress!.CycleId;
        Assert.False(repository.CycleCompleted);
        Assert.Equal(2, repository.Progress.NextPage);

        await Create(repository).RunAsync(connector, ScrapeMode.Full, TestContext.Current.CancellationToken);
        Assert.True(repository.CycleCompleted);
        Assert.Equal(cycle, repository.Progress.CycleId);
        Assert.Equal(nextUri, connector.Requests[1].PageUri);

        await Create(repository).RunAsync(connector, ScrapeMode.Full, TestContext.Current.CancellationToken);
        Assert.Equal([1, 2, 1], connector.Requests.Select(request => request.Page));
        Assert.NotEqual(cycle, repository.Progress.CycleId);
        Assert.Equal(["a", "z", "a"], repository.PublishedExternalIds);
    }

    [Fact]
    public async Task Failed_page_preserves_previous_checkpoint_and_does_not_complete_the_cycle()
    {
        var repository = new FakeRepository();
        var connector = new FakeConnector(
            new ScrapePage([Listing("a")], 2),
            new HttpRequestException("page failed"),
            new ScrapePage([Listing("z")], null));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Create(repository, 2).RunAsync(connector, ScrapeMode.Full, TestContext.Current.CancellationToken));
        Assert.False(repository.CycleCompleted);
        Assert.True(repository.Failed);
        Assert.Equal(1, repository.FailedItemsSeen);
        Assert.Equal(1, repository.FailedItemsChanged);
        Assert.Equal(Now, repository.FinishedAt);
        Assert.Equal(2, repository.Progress!.NextPage);

        await Create(repository).RunAsync(connector, ScrapeMode.Full, TestContext.Current.CancellationToken);
        Assert.Equal([1, 2, 2], connector.Requests.Select(request => request.Page));
        Assert.True(repository.CycleCompleted);
    }

    [Fact]
    public async Task Uncommitted_page_is_retried_without_advancing_the_cursor()
    {
        var repository = new FakeRepository { FailSave = true };
        var page = new ScrapePage([Listing("a")], 2);
        var connector = new FakeConnector(page, page);
        await Assert.ThrowsAsync<IOException>(() =>
            Create(repository).RunAsync(connector, ScrapeMode.Full, TestContext.Current.CancellationToken));
        Assert.Equal(1, repository.Progress!.NextPage);
        Assert.Empty(repository.PublishedExternalIds);

        repository.FailSave = false;
        await Create(repository).RunAsync(connector, ScrapeMode.Full, TestContext.Current.CancellationToken);
        Assert.Equal([1, 1], connector.Requests.Select(request => request.Page));
        Assert.Equal(2, repository.Progress.NextPage);
    }

    [Fact]
    public async Task Repeated_page_across_batches_fails_without_advancing_or_reconciling()
    {
        var repository = new FakeRepository();
        var connector = new FakeConnector(
            new ScrapePage([Listing("a")], 2, Fingerprint: "same-products"),
            new ScrapePage([Listing("a")], null, Fingerprint: "same-products"));
        await Create(repository).RunAsync(connector, ScrapeMode.Full, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Create(repository).RunAsync(connector, ScrapeMode.Full, TestContext.Current.CancellationToken));
        Assert.Equal(2, repository.Progress!.NextPage);
        Assert.False(repository.CycleCompleted);
        Assert.Single(repository.PublishedExternalIds);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Pagination_must_advance_exactly_one_page(int nextPage)
    {
        var repository = new FakeRepository();
        var connector = new FakeConnector(new ScrapePage([Listing("a")], nextPage));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Create(repository).RunAsync(connector, ScrapeMode.Full, TestContext.Current.CancellationToken));
        Assert.Empty(repository.PublishedExternalIds);
        Assert.Equal(1, repository.Progress!.NextPage);
    }

    [Fact]
    public async Task Interrupted_execution_is_recorded_and_resumes_the_pending_page()
    {
        var repository = new FakeRepository();
        var connector = new FakeConnector(
            new ScrapePage([Listing("a")], 2),
            new OperationCanceledException(),
            new ScrapePage([Listing("z")], null));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            Create(repository, 2).RunAsync(connector, ScrapeMode.Full, TestContext.Current.CancellationToken));
        Assert.True(repository.Failed);
        Assert.Equal(2, repository.Progress!.NextPage);
        await Create(repository).RunAsync(connector, ScrapeMode.Full, TestContext.Current.CancellationToken);
        Assert.Equal([1, 2, 2], connector.Requests.Select(request => request.Page));
    }

    [Fact]
    public async Task Failed_completion_resumes_finalization_without_refetching_the_catalog()
    {
        var repository = new FakeRepository { FailComplete = true };
        var connector = new FakeConnector(new ScrapePage([Listing("a")], null));
        await Assert.ThrowsAsync<IOException>(() =>
            Create(repository).RunAsync(connector, ScrapeMode.Full, TestContext.Current.CancellationToken));
        Assert.Null(repository.Progress!.NextPage);
        Assert.False(repository.CycleCompleted);

        repository.FailComplete = false;
        await Create(repository).RunAsync(connector, ScrapeMode.Full, TestContext.Current.CancellationToken);
        Assert.Single(connector.Requests);
        Assert.True(repository.CycleCompleted);
    }

    [Fact]
    public async Task Explicit_stock_and_unknown_stock_are_saved_with_the_completed_page()
    {
        var repository = new FakeRepository();
        var connector = new FakeConnector(new ScrapePage(
            [Listing("available"), Listing("unavailable", 0), Listing("unknown", null)], 2));
        await Create(repository).RunAsync(connector, ScrapeMode.Full, TestContext.Current.CancellationToken);
        Assert.Equal(["available", "unavailable", "unknown"], repository.PublishedExternalIds);
        Assert.False(repository.CycleCompleted);
    }

    [Fact]
    public async Task Lease_owned_by_another_worker_skips_the_attempt()
    {
        var repository = new FakeRepository { LeaseAvailable = false };
        var connector = new FakeConnector();
        await Create(repository).RunAsync(connector, ScrapeMode.Full, TestContext.Current.CancellationToken);
        Assert.Null(repository.Progress);
        Assert.Empty(connector.Requests);
    }

    private static ScrapeOrchestrator Create(FakeRepository repository, int pages = 1) => new(
        repository, new FixedTimeProvider(Now), NullLogger<ScrapeOrchestrator>.Instance,
        new ScrapeBatchOptions { MaxPagesPerRun = pages });

    internal static ExternalListing Listing(string externalId, int? quantity = 1) => new(
        externalId, "Charizard", "base", "Base Set", "4", "pt-BR", CardFinish.Holo, null,
        "Charizard (#4)", new Uri($"https://example.test/{externalId}"), new Money(100, "BRL"),
        CardCondition.NearMint, quantity, new Dictionary<string, string>());

    private sealed class FakeConnector(params object[] pages) : IStoreConnector
    {
        private int _index;
        public Guid StoreId { get; } = Guid.NewGuid();
        public string Key => "fake";
        public List<ScrapeRequest> Requests { get; } = [];

        public Task<ScrapePage> FetchAsync(ScrapeRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return pages[_index++] switch
            {
                ScrapePage page => Task.FromResult(page),
                Exception exception => Task.FromException<ScrapePage>(exception),
                _ => throw new InvalidOperationException()
            };
        }
    }

    private sealed class FakeRepository : IScrapeRepository
    {
        public List<string> PublishedExternalIds { get; } = [];
        public ScrapeProgress? Progress { get; private set; }
        public bool CycleCompleted { get; private set; }
        public bool Failed { get; private set; }
        public bool FailSave { get; set; }
        public bool FailComplete { get; set; }
        public bool LeaseAvailable { get; set; } = true;
        public int FailedItemsSeen { get; private set; }
        public int FailedItemsChanged { get; private set; }
        public DateTimeOffset? FinishedAt { get; private set; }

        public Task<IAsyncDisposable?> TryAcquireStoreLeaseAsync(Guid storeId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IAsyncDisposable?>(LeaseAvailable ? new NoOpLease() : null);

        public Task<ScrapeExecution> StartAsync(Guid storeId, ScrapeMode mode, DateTimeOffset startedAt,
            CancellationToken cancellationToken = default)
        {
            if (Progress is null || CycleCompleted)
                Progress = new ScrapeProgress(Guid.NewGuid(), startedAt);
            CycleCompleted = false;
            return Task.FromResult(new ScrapeExecution(Guid.NewGuid(), storeId, mode, Progress));
        }

        public Task<int> SavePageAsync(ScrapeExecution execution, IReadOnlyCollection<ExternalListing> listings,
            ScrapeProgress progress, int itemsSeen, int itemsChanged, DateTimeOffset observedAt,
            CancellationToken cancellationToken = default)
        {
            if (FailSave)
                throw new IOException("checkpoint failed");
            PublishedExternalIds.AddRange(listings.Select(listing => listing.ExternalId));
            Progress = progress;
            return Task.FromResult(listings.Count);
        }

        public Task CompleteAsync(ScrapeExecution execution, ScrapeProgress progress, int itemsSeen, int itemsChanged,
            DateTimeOffset finishedAt, CancellationToken cancellationToken = default)
        {
            if (FailComplete)
                throw new IOException("completion failed");
            CycleCompleted = progress.NextPage is null;
            return Task.CompletedTask;
        }

        public Task FailAsync(ScrapeExecution execution, string errorCode, int itemsSeen, int itemsChanged,
            DateTimeOffset finishedAt, CancellationToken cancellationToken = default)
        {
            Failed = true;
            FailedItemsSeen = itemsSeen;
            FailedItemsChanged = itemsChanged;
            FinishedAt = finishedAt;
            return Task.CompletedTask;
        }

        public Task<int> PurgeUnavailableAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        private sealed class NoOpLease : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
