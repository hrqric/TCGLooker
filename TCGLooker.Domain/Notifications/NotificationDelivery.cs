namespace TCGLooker.Domain.Notifications;

public enum NotificationChannelType
{
    Telegram,
    WhatsApp
}

public enum NotificationDeliveryStatus
{
    Pending,
    Sent,
    Failed
}

public sealed class NotificationDelivery
{
    public required Guid Id { get; init; }
    public required Guid WishlistItemId { get; init; }
    public required Guid ListingId { get; init; }
    public required Guid ChannelId { get; init; }
    public required string EventType { get; init; }
    public required long AvailabilityVersion { get; init; }
    public NotificationDeliveryStatus Status { get; private set; } = NotificationDeliveryStatus.Pending;
    public int Attempts { get; private set; }
    public DateTimeOffset? NextAttemptAt { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }

    public void MarkSent(DateTimeOffset sentAt)
    {
        Status = NotificationDeliveryStatus.Sent;
        SentAt = sentAt;
        NextAttemptAt = null;
    }

    public void ScheduleRetry(DateTimeOffset nextAttemptAt)
    {
        Attempts++;
        Status = NotificationDeliveryStatus.Pending;
        NextAttemptAt = nextAttemptAt;
    }
}
