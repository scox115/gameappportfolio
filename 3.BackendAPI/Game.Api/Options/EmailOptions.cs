using System.ComponentModel.DataAnnotations;

namespace Game.Api.Options;

public enum EmailProvider
{
    /// <summary>No email: account recovery is switched off, and the game says so.</summary>
    None,

    /// <summary>Local development: emails go to the log, so recovery links can be copied from the console.</summary>
    Log,

    /// <summary>Azure Communication Services, signed in with the API's managed identity.</summary>
    AzureCommunicationServices
}

// Bound from the "Email" configuration section; see docs/adr/0022-account-recovery-by-email.md.
public class EmailOptions : IValidatableObject
{
    public const string SectionName = "Email";

    public EmailProvider Provider { get; set; } = EmailProvider.None;

    /// <summary>The Communication Services endpoint, such as https://acs-cardarena.unitedstates.communication.azure.com.</summary>
    public string? Endpoint { get; set; }

    /// <summary>The address emails come from, such as DoNotReply@&lt;guid&gt;.azurecomm.net.</summary>
    public string? Sender { get; set; }

    /// <summary>Where the game is played, for the links in emails, such as https://play.scottcoxdev.com.</summary>
    public string? ClientBaseUrl { get; set; }

    /// <summary>How long a password reset link works. Short, since it lets someone into the account.</summary>
    [Range(typeof(TimeSpan), "00:05:00", "1.00:00:00")]
    public TimeSpan ResetLinkLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How long an email confirmation link works.</summary>
    [Range(typeof(TimeSpan), "00:05:00", "7.00:00:00")]
    public TimeSpan ConfirmLinkLifetime { get; set; } = TimeSpan.FromHours(24);

    /// <summary>The least time between two emails of the same kind to one account, so nobody can flood an inbox.</summary>
    [Range(typeof(TimeSpan), "00:00:00", "01:00:00")]
    public TimeSpan ResendCooldown { get; set; } = TimeSpan.FromMinutes(2);

    public bool Enabled => Provider != EmailProvider.None;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Enabled && !Uri.TryCreate(ClientBaseUrl, UriKind.Absolute, out _))
            yield return new ValidationResult("Email:ClientBaseUrl must be the game's address, for the links in emails.", [nameof(ClientBaseUrl)]);

        if (Provider == EmailProvider.AzureCommunicationServices)
        {
            if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out _))
                yield return new ValidationResult("Email:Endpoint must be the Communication Services endpoint.", [nameof(Endpoint)]);
            if (string.IsNullOrWhiteSpace(Sender))
                yield return new ValidationResult("Email:Sender must be the address emails come from.", [nameof(Sender)]);
        }
    }
}
