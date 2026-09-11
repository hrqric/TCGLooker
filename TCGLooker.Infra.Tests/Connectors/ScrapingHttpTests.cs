using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using TCGLooker.Application.Ingestion;
using TCGLooker.Infra.Connectors;
using Xunit;

namespace TCGLooker.Infra.Tests.Connectors;

public sealed class ScrapingHttpTests : IDisposable
{
    private readonly string _state = Path.Combine(Path.GetTempPath(), $"tcg-scraping-{Guid.NewGuid():N}.json");
    private readonly ManualTime _time = new();
    private ScrapingHttpOptions Options() => new() { CooldownStatePath = _state, JitterMilliseconds = 0 };

    [Fact]
    public async Task Separate_clients_share_host_and_global_spacing_including_failed_connections()
    {
        var options = Options();
        options.MaxRetries = 0;
        var coordinator = new ScrapingRequestCoordinator(options, _time);
        var times = new List<DateTimeOffset>();
        using var transport = new StubHandler((request, _) =>
        {
            times.Add(_time.GetUtcNow());
            if (times.Count == 1)
                throw new HttpRequestException("Network failure");
            return Task.FromResult(Ok(request));
        });
        using var first = Client(options, coordinator, transport);
        using var second = Client(options, coordinator, transport);
        await Assert.ThrowsAsync<HttpRequestException>(() => first.GetAsync("https://store.example/1", TestContext.Current.CancellationToken));
        var sameHost = second.GetAsync("https://store.example/2", TestContext.Current.CancellationToken);
        Assert.False(sameHost.IsCompleted);
        _time.Advance(TimeSpan.FromSeconds(9));
        Assert.Single(times);
        _time.Advance(TimeSpan.FromSeconds(1));
        using var response = await sameHost;
        var otherHost = first.GetAsync("https://another.example/1", TestContext.Current.CancellationToken);
        Assert.False(otherHost.IsCompleted);
        _time.Advance(TimeSpan.FromSeconds(5));
        using var other = await otherHost;
        Assert.Equal(TimeSpan.FromSeconds(10), times[1] - times[0]);
        Assert.Equal(TimeSpan.FromSeconds(5), times[2] - times[1]);
    }

    [Theory]
    [InlineData(403, false, 24)]
    [InlineData(429, false, 2)]
    [InlineData(429, true, 2)]
    [InlineData(503, true, 2)]
    public async Task Restrictions_do_not_retry_and_survive_coordinator_recreation(int status, bool date, int hours)
    {
        var options = Options();
        var calls = 0;
        var start = _time.GetUtcNow();
        using var transport = new StubHandler((request, _) =>
        {
            calls++;
            var response = new HttpResponseMessage((HttpStatusCode)status) { RequestMessage = request };
            response.Headers.RetryAfter = date
                ? new RetryConditionHeaderValue(start.AddHours(2))
                : new RetryConditionHeaderValue(TimeSpan.FromHours(2));
            return Task.FromResult(response);
        });
        using var first = Client(options, new ScrapingRequestCoordinator(options, _time), transport);
        var blocked = await Assert.ThrowsAsync<ScrapeDeferredException>(() => first.GetAsync("https://store.example/", TestContext.Current.CancellationToken));
        Assert.Equal(start.AddHours(hours), blocked.RetryAt);
        using var restarted = Client(options, new ScrapingRequestCoordinator(options, _time), transport);
        await Assert.ThrowsAsync<ScrapeDeferredException>(() => restarted.GetAsync("https://store.example/other", TestContext.Current.CancellationToken));
        Assert.Equal(1, calls);

        _time.Advance(TimeSpan.FromHours(hours));
        await Assert.ThrowsAsync<ScrapeDeferredException>(() => restarted.GetAsync("https://store.example/", TestContext.Current.CancellationToken));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Rate_limit_without_retry_after_uses_default_pause_and_other_hosts_can_continue()
    {
        var options = Options();
        using var transport = new StubHandler((request, _) => Task.FromResult(
            request.RequestUri!.Host == "store.example"
                ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                : Ok(request)));
        using var client = Client(options, new ScrapingRequestCoordinator(options, _time), transport);
        var blocked = await Assert.ThrowsAsync<ScrapeDeferredException>(() => client.GetAsync("https://store.example/", TestContext.Current.CancellationToken));
        Assert.Equal(_time.GetUtcNow().AddMinutes(30), blocked.RetryAt);
        var other = client.GetAsync("https://other.example/", TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(5));
        using var response = await other;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Redirects_are_rate_limited_and_external_redirects_are_never_sent()
    {
        var options = Options();
        var calls = new List<Uri>();
        using var transport = new StubHandler((request, _) =>
        {
            calls.Add(request.RequestUri!);
            var response = new HttpResponseMessage(HttpStatusCode.Redirect) { RequestMessage = request };
            response.Headers.Location = new Uri(calls.Count == 1 ? "/next" : "https://elsewhere.example/", UriKind.RelativeOrAbsolute);
            return Task.FromResult(response);
        });
        using var client = Client(options, new ScrapingRequestCoordinator(options, _time), transport);
        var fetch = client.GetAsync("https://store.example/", TestContext.Current.CancellationToken);
        Assert.Single(calls);
        _time.Advance(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<HttpRequestException>(() => fetch);
        Assert.Equal(2, calls.Count);
        Assert.All(calls, uri => Assert.Equal("store.example", uri.Host));
    }

    [Fact]
    public async Task Cancellation_while_queued_does_not_send_and_releases_permit()
    {
        var options = Options();
        var calls = 0;
        using var transport = new StubHandler((request, _) => { calls++; return Task.FromResult(Ok(request)); });
        using var client = Client(options, new ScrapingRequestCoordinator(options, _time), transport);
        using var first = await client.GetAsync("https://store.example/", TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        var waiting = client.GetAsync("https://store.example/2", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(1, calls);
        var next = client.GetAsync("https://store.example/3", TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(10));
        using var response = await next;
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Server_failure_retries_are_bounded_and_count_against_rate_limit()
    {
        var options = Options();
        var calls = 0;
        using var transport = new StubHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        });
        using var client = Client(options, new ScrapingRequestCoordinator(options, _time), transport);
        var fetch = client.GetAsync("https://store.example/", TestContext.Current.CancellationToken);
        // Advance the virtual clock until all retry/backoff waits have been registered and elapsed.
        for (var tick = 0; tick < 100 && !fetch.IsCompleted; tick++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }
        using var response = await fetch.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Final_network_timeout_is_a_failure_instead_of_worker_cancellation()
    {
        var options = Options();
        options.MaxRetries = 0;
        using var transport = new StubHandler(async (request, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Ok(request);
        });
        using var client = Client(options, new ScrapingRequestCoordinator(options, _time), transport);
        var fetch = client.GetAsync("https://store.example/", TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(30));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => fetch);
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
    }

    [Fact]
    public async Task Concurrent_hosts_have_only_one_network_request_in_flight()
    {
        var options = Options();
        var calls = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var transport = new StubHandler(async (request, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                await release.Task.WaitAsync(token);
            return Ok(request);
        });
        var coordinator = new ScrapingRequestCoordinator(options, _time);
        using var first = Client(options, coordinator, transport);
        using var second = Client(options, coordinator, transport);
        var one = first.GetAsync("https://store.example/", TestContext.Current.CancellationToken);
        var two = second.GetAsync("https://other.example/", TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(1, calls);
        release.SetResult();
        using var firstResponse = await one;
        using var secondResponse = await two;
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Corrupt_persisted_cooldown_prevents_network_access()
    {
        await File.WriteAllTextAsync(_state, "invalid-json", TestContext.Current.CancellationToken);
        var options = Options();
        var calls = 0;
        using var transport = new StubHandler((request, _) => { calls++; return Task.FromResult(Ok(request)); });
        using var client = Client(options, new ScrapingRequestCoordinator(options, _time), transport);
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => client.GetAsync("https://store.example/", TestContext.Current.CancellationToken));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Proxy_credentials_are_separate_and_disabled_proxy_ignores_system_defaults()
    {
        var options = Options();
        using var direct = PublicInternetHttpHandler.Create(false, options.Proxy);
        Assert.False(direct.UseProxy);
        Assert.False(direct.AllowAutoRedirect);
        options.Proxy = new() { Enabled = true, Url = "http://127.0.0.1:8080", Username = "user", Password = "secret" };
        options.Validate();
        using var proxied = PublicInternetHttpHandler.Create(false, options.Proxy);
        Assert.True(proxied.UseProxy);
        var proxy = Assert.IsType<WebProxy>(proxied.Proxy);
        Assert.False(proxy.BypassProxyOnLocal);
        Assert.Equal(new Uri("http://127.0.0.1:8080"), proxy.GetProxy(new Uri("https://store.example")));
        Assert.Equal("user", Assert.IsType<NetworkCredential>(proxy.Credentials).UserName);
    }

    [Theory]
    [InlineData("http://user:secret@proxy.example:8080")]
    [InlineData("socks5://proxy.example:1080")]
    [InlineData("http://proxy.example/path")]
    [InlineData("")]
    public void Enabled_proxy_requires_valid_configuration(string url)
    {
        var options = Options();
        options.Proxy = new() { Enabled = true, Url = url };
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Zero_rate_limit_is_rejected()
    {
        var options = Options();
        options.RequestsPerMinute = 0;
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    private HttpClient Client(ScrapingHttpOptions options, ScrapingRequestCoordinator coordinator, HttpMessageHandler transport) =>
        new(new ScrapingHttpHandler(coordinator, options, _time, NullLogger<ScrapingHttpHandler>.Instance)
        { InnerHandler = transport }) { Timeout = Timeout.InfiniteTimeSpan };

    private static HttpResponseMessage Ok(HttpRequestMessage request) => new(HttpStatusCode.OK)
    { RequestMessage = request, Content = new StringContent("<html></html>") };

    public void Dispose()
    {
        File.Delete(_state);
        File.Delete(_state + ".tmp");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class ManualTime : TimeProvider
    {
        private readonly object _sync = new();
        private DateTimeOffset _now = new(2026, 9, 11, 0, 0, 0, TimeSpan.Zero);
        private readonly List<ManualTimer> _timers = [];
        public override DateTimeOffset GetUtcNow() { lock (_sync) return _now; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_sync)
            {
                var timer = new ManualTimer(this, callback, state);
                timer.Change(dueTime, period);
                _timers.Add(timer);
                return timer;
            }
        }
        public void Advance(TimeSpan amount)
        {
            ManualTimer[] ready;
            lock (_sync)
            {
                _now += amount;
                ready = _timers.Where(t => t.Due <= _now).ToArray();
                foreach (var timer in ready) timer.Due = DateTimeOffset.MaxValue;
            }
            foreach (var timer in ready) timer.Fire();
        }
        private sealed class ManualTimer(ManualTime clock, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset Due { get; set; }
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._sync)
                {
                    if (_disposed) return false;
                    Due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock._now + dueTime;
                    return true;
                }
            }
            public void Fire() { if (!_disposed) callback(state); }
            public void Dispose() { lock (clock._sync) { _disposed = true; clock._timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}

