namespace TCGLooker.Application.Notifications;

/// <summary>
/// Integration point for future Telegram, WhatsApp or other delivery providers.
/// Providers are intentionally not registered until their credentials and destination
/// decryption strategy are configured.
/// </summary>
public interface INotificationSender
{
    string ChannelType { get; }

    Task SendAsync(
        NotificationMessage message,
        CancellationToken cancellationToken = default);
}

public sealed record NotificationMessage(
    Guid DeliveryId,
    Guid WishlistItemId,
    Guid ListingId,
    string Destination,
    string Title,
    string Body,
    Uri OfferUrl);

