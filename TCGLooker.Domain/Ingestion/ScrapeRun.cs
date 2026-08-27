namespace TCGLooker.Domain.Ingestion;

public enum ScrapeRunStatus
{
    Running,
    Succeeded,
    PartiallySucceeded,
    Failed
}

public sealed class ScrapeRun
{
    public required Guid Id { get; init; }
    public required Guid StoreId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public ScrapeRunStatus Status { get; private set; } = ScrapeRunStatus.Running;
    public int ItemsSeen { get; private set; }
    public int ItemsChanged { get; private set; }
    public string? ErrorCode { get; private set; }

    public void Complete(int itemsSeen, int itemsChanged, DateTimeOffset finishedAt)
    {
        ItemsSeen = itemsSeen;
        ItemsChanged = itemsChanged;
        FinishedAt = finishedAt;
        Status = ScrapeRunStatus.Succeeded;
    }

    public void Fail(string errorCode, DateTimeOffset finishedAt)
    {
        ErrorCode = errorCode;
        FinishedAt = finishedAt;
        Status = ScrapeRunStatus.Failed;
    }
}
