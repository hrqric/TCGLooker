using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TCGLooker.Application.Notifications;

namespace TCGLooker.Worker;

internal sealed class NotificationWorker(
    IConfiguration configuration,
    INotificationOutboxProcessor processor,
    ILogger<NotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollingSeconds = Math.Max(
            1,
            configuration.GetValue("Notifications:OutboxPollingSeconds", 15));
        var batchSize = Math.Clamp(
            configuration.GetValue("Notifications:OutboxBatchSize", 100),
            1,
            1000);

        while (!stoppingToken.IsCancellationRequested)
        {
            var filledBatch = await ProcessBatchAsync(batchSize, stoppingToken);
            if (filledBatch)
                continue;

            await Task.Delay(TimeSpan.FromSeconds(pollingSeconds), stoppingToken);
        }
    }

    private async Task<bool> ProcessBatchAsync(
        int batchSize,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await processor.ProcessAsync(batchSize, cancellationToken);
            if (result.EventsProcessed > 0)
            {
                logger.LogInformation(
                    "Processed {EventCount} wishlist events and created {DeliveryCount} notification deliveries",
                    result.EventsProcessed,
                    result.DeliveriesCreated);
            }

            return result.EventsProcessed == batchSize;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not process the notification outbox");
            return false;
        }
    }
}

