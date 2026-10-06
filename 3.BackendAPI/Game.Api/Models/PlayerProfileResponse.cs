using Game.Core.Entities;

namespace Game.Api.Models;

public record PlayerProfileResponse(
    Guid Id, string Username, int Gold, int Level, int ExperiencePoints, string? AvatarUrl, int Rating, int PvpWins, int PvpLosses)
{
    public static PlayerProfileResponse From(Player player) =>
        new(player.Id, player.Username, player.Gold, player.Level, player.ExperiencePoints, player.AvatarUrl,
            player.Rating, player.PvpWins, player.PvpLosses);
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
