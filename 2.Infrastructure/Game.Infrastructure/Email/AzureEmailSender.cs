using Azure;
using Azure.Communication.Email;
using Game.Core.Interfaces;

namespace Game.Infrastructure.Email;

/// <summary>
/// Sends email through Azure Communication Services. The API signs in with its managed identity
/// (the deployment grants it "Communication and Email Service Owner" on the resource), so there is
/// no connection string or key anywhere.
/// </summary>
public class AzureEmailSender(EmailClient client, string senderAddress) : IEmailSender
{
    public async Task SendAsync(Game.Core.Interfaces.EmailMessage message, CancellationToken cancellationToken = default)
    {
        var email = new Azure.Communication.Email.EmailMessage(
            senderAddress,
            message.To,
            new EmailContent(message.Subject) { PlainText = message.PlainText, Html = message.Html });

        // Started rather than awaited to completion: the service queues it, and a player shouldn't wait for delivery.
        await client.SendAsync(WaitUntil.Started, email, cancellationToken);
    }
}
