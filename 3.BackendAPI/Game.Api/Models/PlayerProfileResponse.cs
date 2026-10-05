using Game.Core.Entities;

namespace Game.Api.Models;

public record PlayerProfileResponse(Guid Id, string Username, int Gold, int Level, int ExperiencePoints, string? AvatarUrl)
{
    public static PlayerProfileResponse From(Player player) =>
        new(player.Id, player.Username, player.Gold, player.Level, player.ExperiencePoints, player.AvatarUrl);
}

/// <summary>
/// A signed-in session. Use the access token on API calls until ExpiresAt, then trade the refresh
/// token at /api/auth/refresh for a new pair; each refresh token works once.
/// </summary>
public record AuthResponse(
    string AccessToken,
    DateTimeOffset ExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    PlayerProfileResponse Player);
