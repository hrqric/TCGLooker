using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TCGLooker.Application.Ingestion;
using TCGLooker.Application.Stores;
using TCGLooker.Worker;
using Xunit;

namespace TCGLooker.Infra.Tests.Ingestion;

public sealed class StoreScrapeSchedulerTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly StoreSource Source = new(Guid.NewGuid(), "fake", "Fake",
        new Uri("https://example.test/"), "fake", "liga_magic", StoreScope.Global, null);

    [Fact]
    public async Task Discovery_keeps_its_schedule_while_refresh_runs_in_the_gaps()
    {
        var time = new AdvancingTime(Start);
        using var stop = new CancellationTokenSource();
        var discoveryTimes = new List<DateTimeOffset>();
        var refreshTimes = new List<DateTimeOffset>();
        var discovery = new Discovery(() =>
        {
            discoveryTimes.Add(time.GetUtcNow());
            time.Advance(TimeSpan.FromMinutes(2));
            if (discoveryTimes.Count == 3) stop.Cancel();
        });
        var refresh = new Refresh(deadline =>
        {
            Assert.True(time.GetUtcNow() < deadline);
            refreshTimes.Add(time.GetUtcNow());
            time.Advance(TimeSpan.FromMinutes(1));
        });
        await Create(time, discovery, refresh).RunStoreAsync(Source, new Connector(), stop.Token);
        Assert.Equal([0, 17, 34], discoveryTimes.Select(t => (int)(t - Start).TotalMinutes));
        Assert.Equal([2, 8, 14, 20, 26, 32], refreshTimes.Select(t => (int)(t - Start).TotalMinutes));
    }

    [Fact]
    public async Task A_long_refresh_gives_discovery_priority_as_soon_as_it_finishes()
    {
        var time = new AdvancingTime(Start);
        using var stop = new CancellationTokenSource();
        var discoveryTimes = new List<DateTimeOffset>();
        var refreshCalls = 0;
        var discovery = new Discovery(() =>
        {
            discoveryTimes.Add(time.GetUtcNow());
            if (discoveryTimes.Count == 2) stop.Cancel();
        });
        var refresh = new Refresh(deadline =>
        {
            refreshCalls++;
            time.Advance(deadline - time.GetUtcNow() + TimeSpan.FromMinutes(1));
        });
        await Create(time, discovery, refresh).RunStoreAsync(Source, new Connector(), stop.Token);
        Assert.Equal([0, 16], discoveryTimes.Select(t => (int)(t - Start).TotalMinutes));
        Assert.Equal(1, refreshCalls);
    }

    [Fact]
    public async Task Restriction_during_refresh_pauses_discovery_too()
    {
        var time = new AdvancingTime(Start);
        using var stop = new CancellationTokenSource();
        var discoveryTimes = new List<DateTimeOffset>();
        var discovery = new Discovery(() =>
        {
            discoveryTimes.Add(time.GetUtcNow());
            if (discoveryTimes.Count == 2) stop.Cancel();
        });
        var refresh = new Refresh(_ => throw new ScrapeDeferredException(
            time.GetUtcNow().AddHours(1), HttpStatusCode.TooManyRequests));
        await Create(time, discovery, refresh).RunStoreAsync(Source, new Connector(), stop.Token);
        Assert.Equal([0, 60], discoveryTimes.Select(t => (int)(t - Start).TotalMinutes));
    }

    [Fact]
    public async Task Refresh_can_be_disabled_without_stopping_discovery()
    {
        var time = new AdvancingTime(Start);
        using var stop = new CancellationTokenSource();
        var calls = 0;
        var discovery = new Discovery(() => { if (++calls == 2) stop.Cancel(); });
        var refresh = new Refresh(_ => Assert.Fail("Refresh is disabled."));
        await Create(time, discovery, refresh, enabled: false).RunStoreAsync(Source, new Connector(), stop.Token);
        Assert.Equal(2, calls);
        Assert.Equal(Start.AddMinutes(15), time.GetUtcNow());
    }

    private static ScrapeSchedulerWorker Create(TimeProvider time, IScrapeOrchestrator discovery,
        IProductRefreshOrchestrator refresh, bool enabled = true) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Scraping:Refresh:Enabled"] = enabled.ToString()
        }).Build(), null!, null!, discovery, refresh, time, NullLogger<ScrapeSchedulerWorker>.Instance);

    private sealed class Discovery(Action run) : IScrapeOrchestrator
    {
        public Task RunAsync(IStoreConnector connector, ScrapeMode mode, CancellationToken cancellationToken = default)
        {
            Assert.Equal(ScrapeMode.Full, mode);
            run();
            return Task.CompletedTask;
        }
    }

    private sealed class Refresh(Action<DateTimeOffset> run) : IProductRefreshOrchestrator
    {
        public Task RunAsync(IProductStoreConnector connector, DateTimeOffset yieldAt, CancellationToken cancellationToken = default)
        {
            run(yieldAt);
            return Task.CompletedTask;
        }
    }

    private sealed class Connector : IProductStoreConnector
    {
        public Guid StoreId => Source.Id;
        public string Key => "fake";
        public Task<ScrapePage> FetchAsync(ScrapeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyCollection<ExternalListing>> FetchProductAsync(Uri productUri, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    // Single-store tests have one outstanding scheduling delay. Advance that delay
    // immediately, while scheduling its callback asynchronously like a real timer.
    private sealed class AdvancingTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.True(dueTime > TimeSpan.Zero);
            Advance(dueTime);
            return new Timer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }
}
