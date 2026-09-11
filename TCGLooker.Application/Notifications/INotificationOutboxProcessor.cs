namespace TCGLooker.Application.Notifications;

public interface INotificationOutboxProcessor
{
    Task<NotificationOutboxBatch> ProcessAsync(
        int batchSize,
        CancellationToken cancellationToken = default);
}

public sealed record NotificationOutboxBatch(int EventsProcessed, int DeliveriesCreated);

