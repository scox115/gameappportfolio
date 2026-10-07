namespace Game.Core.Interfaces;

/// <summary>An email to one player: a subject, and the same message as plain text and HTML.</summary>
public record EmailMessage(string To, string Subject, string PlainText, string Html);

/// <summary>Sends email, such as account recovery links. See docs/adr/0022-account-recovery-by-email.md.</summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}
