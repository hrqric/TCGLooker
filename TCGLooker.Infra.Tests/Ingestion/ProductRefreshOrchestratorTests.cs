using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using TCGLooker.Application.Ingestion;
using TCGLooker.Infra.Ingestion;
using Xunit;

namespace TCGLooker.Infra.Tests.Ingestion;

public sealed class ProductRefreshOrchestratorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Batch_reserves_oldest_slots_and_checks_age_without_touching_discovery_progress()
    {
        var repository = new FakeRepository(11);
        var connector = new FakeConnector();
        await Create(repository).RunAsync(connector, Now.AddMinutes(15), TestContext.Current.CancellationToken);

        Assert.Equal(10, repository.Saved.Count);
        Assert.Equal([false, true, true, true, false, true, true, true, false, true], repository.Preferences);
        Assert.All(repository.Cutoffs, cutoff => Assert.Equal(Now.AddMinutes(-150), cutoff));
        Assert.All(repository.RetryCutoffs, cutoff => Assert.Equal(Now.AddMinutes(-30), cutoff));
        Assert.All(repository.Saved, listings => Assert.Single(listings)); // duplicate offers deduplicated
        Assert.True(repository.LeaseDisposed);
    }

    [Fact]
    public async Task Refresh_yields_between_products_when_discovery_becomes_due()
    {
        var repository = new FakeRepository(10);
        var time = new MutableTime(Now);
        var connector = new FakeConnector { OnFetch = () => time.Now = time.Now.AddMinutes(1) };
        await Create(repository, time).RunAsync(connector, Now.AddSeconds(90), TestContext.Current.CancellationToken);
        Assert.Equal(2, repository.Saved.Count);
    }

    [Fact]
    public async Task Failure_is_audited_and_other_products_continue()
    {
        var repository = new FakeRepository(2);
        var connector = new FakeConnector { FirstFailure = new HttpRequestException("Timeout") };
        await Create(repository).RunAsync(connector, Now.AddMinutes(15), TestContext.Current.CancellationToken);
        Assert.Equal(2, repository.Started.Count);
        Assert.Single(repository.Failed);
        Assert.Single(repository.Saved);
    }

    [Fact]
    public async Task Unrecognized_product_is_not_saved_as_fresh()
    {
        var repository = new FakeRepository(2);
        var connector = new FakeConnector { ReturnEmpty = true };
        await Create(repository).RunAsync(connector, Now.AddMinutes(15), TestContext.Current.CancellationToken);
        Assert.Equal(2, repository.Failed.Count);
        Assert.Empty(repository.Saved);
    }

    [Fact]
    public async Task Store_restriction_stops_refresh_and_propagates_to_the_shared_scheduler()
    {
        var repository = new FakeRepository(2);
        var connector = new FakeConnector { FirstFailure = new HttpRequestException("Forbidden", null, HttpStatusCode.Forbidden) };
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Create(repository).RunAsync(connector, Now.AddMinutes(15), TestContext.Current.CancellationToken));
        Assert.Single(repository.Started);
        Assert.Single(repository.Failed);
        Assert.Empty(repository.Saved);
    }

    [Fact]
    public async Task Cancellation_preserves_the_attempt_and_releases_the_store()
    {
        var repository = new FakeRepository(2);
        var connector = new FakeConnector { FirstFailure = new OperationCanceledException() };
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            Create(repository).RunAsync(connector, Now.AddMinutes(15), TestContext.Current.CancellationToken));
        Assert.Single(repository.Failed);
        Assert.True(repository.LeaseDisposed);
        Assert.Empty(repository.Saved);
    }

    [Fact]
    public async Task Another_store_lease_owner_prevents_refresh()
    {
        var repository = new FakeRepository(2) { LeaseAvailable = false };
        await Create(repository).RunAsync(new FakeConnector(), Now.AddMinutes(15), TestContext.Current.CancellationToken);
        Assert.Empty(repository.Started);
        Assert.Empty(repository.Preferences);
    }

    [Fact]
    public async Task Database_failure_stops_the_batch_without_publishing_success()
    {
        var repository = new FakeRepository(2) { FailSave = true };
        await Assert.ThrowsAsync<IOException>(() =>
            Create(repository).RunAsync(new FakeConnector(), Now.AddMinutes(15), TestContext.Current.CancellationToken));
        Assert.Single(repository.Started);
        Assert.Single(repository.Failed);
        Assert.Empty(repository.Saved);
    }

    private static ProductRefreshOrchestrator Create(FakeRepository repository, TimeProvider? time = null) =>
        new(repository, repository, new ProductRefreshOptions(), time ?? new MutableTime(Now),
            NullLogger<ProductRefreshOrchestrator>.Instance);

    private sealed class MutableTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeConnector : IProductStoreConnector
    {
        public Guid StoreId { get; } = Guid.NewGuid();
        public string Key => "fake";
        public Action? OnFetch { get; init; }
        public Exception? FirstFailure { get; set; }
        public bool ReturnEmpty { get; init; }
        public Task<ScrapePage> FetchAsync(ScrapeRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Refresh must not visit catalog pages.");

        public Task<IReadOnlyCollection<ExternalListing>> FetchProductAsync(Uri uri, CancellationToken cancellationToken = default)
        {
            OnFetch?.Invoke();
            if (FirstFailure is { } exception)
            {
                FirstFailure = null;
                throw exception;
            }
            var listing = ScrapeOrchestratorTests.Listing(uri.Query);
            return Task.FromResult<IReadOnlyCollection<ExternalListing>>(ReturnEmpty ? [] : [listing, listing]);
        }
    }

    private sealed class FakeRepository(int count) : IProductRefreshRepository, IScrapeRepository
    {
        private int _remaining = count;
        public bool LeaseAvailable { get; init; } = true;
        public bool LeaseDisposed { get; private set; }
        public bool FailSave { get; init; }
        public List<bool> Preferences { get; } = [];
        public List<DateTimeOffset> Cutoffs { get; } = [];
        public List<DateTimeOffset> RetryCutoffs { get; } = [];
        public List<Uri> Started { get; } = [];
        public List<Guid> Failed { get; } = [];
        public List<IReadOnlyCollection<ExternalListing>> Saved { get; } = [];

        public Task<ProductRefreshTarget?> FindDueAsync(Guid storeId, DateTimeOffset staleBefore,
            DateTimeOffset retryBefore, bool preferWishlist, CancellationToken cancellationToken = default)
        {
            if (_remaining == 0)
                return Task.FromResult<ProductRefreshTarget?>(null);
            Preferences.Add(preferWishlist);
            Cutoffs.Add(staleBefore);
            RetryCutoffs.Add(retryBefore);
            return Task.FromResult<ProductRefreshTarget?>(new(
                new Uri($"https://example.test/?view=ecom/item&refid={_remaining}"), staleBefore, preferWishlist));
        }

        public Task<ScrapeExecution> StartProductAsync(Guid storeId, Uri productUri, DateTimeOffset startedAt,
            CancellationToken cancellationToken = default)
        {
            _remaining--;
            Started.Add(productUri);
            var run = Guid.NewGuid();
            return Task.FromResult(new ScrapeExecution(run, storeId, ScrapeMode.Incremental, new(run, startedAt)));
        }

        public Task SaveProductAsync(ScrapeExecution execution, IReadOnlyCollection<ExternalListing> listings,
            DateTimeOffset observedAt, CancellationToken cancellationToken = default)
        {
            if (FailSave) throw new IOException("Database unavailable");
            Saved.Add(listings);
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable?> TryAcquireStoreLeaseAsync(Guid storeId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IAsyncDisposable?>(LeaseAvailable ? new Lease(() => LeaseDisposed = true) : null);

        public Task FailAsync(ScrapeExecution execution, string errorCode, int itemsSeen, int itemsChanged,
            DateTimeOffset finishedAt, CancellationToken cancellationToken = default)
        {
            Failed.Add(execution.RunId);
            return Task.CompletedTask;
        }

        public Task<ScrapeExecution> StartAsync(Guid storeId, ScrapeMode mode, DateTimeOffset startedAt,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Discovery cursor must be preserved.");
        public Task<int> SavePageAsync(ScrapeExecution execution, IReadOnlyCollection<ExternalListing> listings,
            ScrapeProgress progress, int itemsSeen, int itemsChanged, DateTimeOffset observedAt,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Discovery cursor must be preserved.");
        public Task CompleteAsync(ScrapeExecution execution, ScrapeProgress progress, int itemsSeen, int itemsChanged,
            DateTimeOffset finishedAt, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No full reconciliation on refresh.");
        public Task<int> PurgeUnavailableAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private sealed class Lease(Action dispose) : IAsyncDisposable
        {
            public ValueTask DisposeAsync() { dispose(); return ValueTask.CompletedTask; }
        }
    }
}
