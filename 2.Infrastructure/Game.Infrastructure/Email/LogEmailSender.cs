using Game.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Game.Infrastructure.Email;

/// <summary>
/// For local development: writes each email to the log instead of sending it, so a developer can
/// copy a recovery link from the console. Never used in Azure, since the log would hold live links.
/// </summary>
public class LogEmailSender(ILogger<LogEmailSender> logger) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        logger.LogWarning("Email to {To}: {Subject}\n{Body}", message.To, message.Subject, message.PlainText);
        return Task.CompletedTask;
    }
}
