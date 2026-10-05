using System.ComponentModel.DataAnnotations;

namespace Game.Api.Options;

// Bound from the "Jwt" configuration section. SigningKey is a secret: user-secrets locally,
// Key Vault (via environment configuration) in hosted environments.
public class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required]
    public string Issuer { get; set; } = string.Empty;

    [Required]
    public string Audience { get; set; } = string.Empty;

    // HMAC-SHA256 needs at least 256 bits of key material.
    [Required, MinLength(32)]
    public string SigningKey { get; set; } = string.Empty;

    // Short, because an access token can't be revoked; clients renew it with a refresh token.
    [Range(1, 1440)]
    public int AccessTokenMinutes { get; set; } = 15;

    [Range(1, 90)]
    public int RefreshTokenDays { get; set; } = 7;
}
