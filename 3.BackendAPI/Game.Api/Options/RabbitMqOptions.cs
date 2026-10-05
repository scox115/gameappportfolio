using System.ComponentModel.DataAnnotations;

namespace Game.Api.Options;

// Bound from the "RabbitMq" configuration section. Credentials come from user-secrets
// locally and from environment variables or Key Vault in hosted environments.
public class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    [Required]
    public string HostName { get; set; } = string.Empty;

    [Range(1, 65535)]
    public int Port { get; set; } = 5672;

    [Required]
    public string VirtualHost { get; set; } = "/";

    [Required]
    public string UserName { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;
}
