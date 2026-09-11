using System.Net;
using System.Text.Json;
using TCGLooker.Application.Ingestion;

namespace TCGLooker.Infra.Connectors;

// Shared by every connector and handler generation in this process.
internal sealed class ScrapingRequestCoordinator(ScrapingHttpOptions options, TimeProvider timeProvider)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> _nextByHost = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Cooldown>? _cooldowns;
    private DateTimeOffset _nextGlobal = DateTimeOffset.MinValue;

    public async Task<HttpResponseMessage> SendAsync(
        Uri uri,
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Fail closed if existing state cannot be read, so a restart cannot silently clear a block.
            _cooldowns ??= File.Exists(options.CooldownStatePath)
                ? JsonSerializer.Deserialize<Dictionary<string, Cooldown>>(
                    await File.ReadAllTextAsync(options.CooldownStatePath, cancellationToken))
                    ?? throw new InvalidDataException("Invalid scraping cooldown state.")
                : new Dictionary<string, Cooldown>();
            var host = uri.IdnHost.ToLowerInvariant();
            var now = timeProvider.GetUtcNow();
            if (_cooldowns.TryGetValue(host, out var cooldown) && cooldown.RetryAt > now)
                throw new ScrapeDeferredException(cooldown.RetryAt, cooldown.StatusCode);

            var due = _nextByHost.GetValueOrDefault(host);
            if (_nextGlobal > due)
                due = _nextGlobal;
            if (due > now)
                await Task.Delay(due - now, timeProvider, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            // Reserve before sending: failed connections and retries consume the same budget.
            now = timeProvider.GetUtcNow();
            var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(options.JitterMilliseconds + 1));
            _nextGlobal = now + RequestInterval(options.GlobalRequestsPerMinute) + jitter;
            _nextByHost[host] = now + RequestInterval(options.RequestsPerMinute) + jitter;

            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(options.RequestTimeoutSeconds), timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            var response = await send(linked.Token);
            try
            {
                var retryAfter = response.Headers.RetryAfter;
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                    || (response.StatusCode == HttpStatusCode.ServiceUnavailable && retryAfter is not null))
                {
                    now = timeProvider.GetUtcNow();
                    var retryAt = now + (response.StatusCode == HttpStatusCode.Forbidden
                        ? TimeSpan.FromHours(options.ForbiddenRetryHours)
                        : TimeSpan.FromMinutes(options.RateLimitRetryMinutes));
                    var serverRetryAt = retryAfter?.Date ?? (now + (retryAfter?.Delta ?? TimeSpan.Zero));
                    if (serverRetryAt > retryAt)
                        retryAt = serverRetryAt;
                    _cooldowns[host] = new Cooldown(retryAt, response.StatusCode);
                    await SaveCooldownsAsync();
                    throw new ScrapeDeferredException(retryAt, response.StatusCode);
                }

                // Buffer while holding the permit so slow response bodies cannot pile up.
                await response.Content.LoadIntoBufferAsync(4 * 1024 * 1024, linked.Token);
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static TimeSpan RequestInterval(int requestsPerMinute) =>
        TimeSpan.FromTicks((long)Math.Ceiling((double)TimeSpan.TicksPerMinute / requestsPerMinute));

    private async Task SaveCooldownsAsync()
    {
        var path = Path.GetFullPath(options.CooldownStatePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        // Preserve a received block even when the worker is being stopped.
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(_cooldowns));
        File.Move(temporary, path, overwrite: true);
    }

    internal sealed record Cooldown(DateTimeOffset RetryAt, HttpStatusCode StatusCode);
}
