using System.Net;

namespace TCGLooker.Infra.Connectors;

internal sealed class ScrapingHttpOptions
{
    public int RequestsPerMinute { get; set; } = 6;
    public int GlobalRequestsPerMinute { get; set; } = 12;
    public int JitterMilliseconds { get; set; } = 500;
    public int RequestTimeoutSeconds { get; set; } = 30;
    public int MaxRetries { get; set; } = 2;
    public int ForbiddenRetryHours { get; set; } = 24;
    public int RateLimitRetryMinutes { get; set; } = 30;
    public string UserAgent { get; set; } = "TCGLooker/0.1";
    public string CooldownStatePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "scraping-cooldowns.json");
    public ScrapingProxyOptions Proxy { get; set; } = new();

    public void Validate()
    {
        if (RequestsPerMinute is < 1 or > 600 || GlobalRequestsPerMinute is < 1 or > 600
            || JitterMilliseconds is < 0 or > 60_000 || RequestTimeoutSeconds is < 1 or > 300
            || MaxRetries is < 0 or > 5 || ForbiddenRetryHours is < 1 or > 8760
            || RateLimitRetryMinutes is < 1 or > 525600 || string.IsNullOrWhiteSpace(CooldownStatePath))
            throw new InvalidOperationException("Invalid Scraping limits, timeouts or cooldown state path.");

        using var request = new HttpRequestMessage();
        if (string.IsNullOrWhiteSpace(UserAgent))
            throw new InvalidOperationException("Scraping:UserAgent must identify the collector.");
        request.Headers.UserAgent.ParseAdd(UserAgent);
        Proxy.Validate();
    }
}

internal sealed class ScrapingProxyOptions
{
    public bool Enabled { get; set; }
    public string? Url { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }

    public void Validate()
    {
        if (!Enabled)
            return;
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https")
            || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || (string.IsNullOrEmpty(Username) != string.IsNullOrEmpty(Password)))
            throw new InvalidOperationException("Invalid Scraping:Proxy configuration; use an HTTP(S) URL and separate credentials.");
    }

    public WebProxy? CreateProxy()
    {
        Validate();
        return Enabled ? new WebProxy(new Uri(Url!))
        {
            BypassProxyOnLocal = false,
            Credentials = string.IsNullOrEmpty(Username) ? null : new NetworkCredential(Username, Password)
        } : null;
    }
}
