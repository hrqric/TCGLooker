using System.Net;

namespace TCGLooker.Application.Ingestion;

public sealed class ScrapeDeferredException(DateTimeOffset retryAt, HttpStatusCode statusCode)
    : HttpRequestException($"Store requests are paused until {retryAt:O} (HTTP {(int)statusCode}).", null, statusCode)
{
    public DateTimeOffset RetryAt { get; } = retryAt;
}
