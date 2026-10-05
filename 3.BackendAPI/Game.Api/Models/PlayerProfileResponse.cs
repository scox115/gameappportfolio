using Game.Core.Entities;

namespace Game.Api.Models;

public record PlayerProfileResponse(Guid Id, string Username, int Gold, int Level, int ExperiencePoints, string? AvatarUrl)
{
    public static PlayerProfileResponse From(Player player) =>
        new(player.Id, player.Username, player.Gold, player.Level, player.ExperiencePoints, player.AvatarUrl);
}

public record AuthResponse(string AccessToken, DateTimeOffset ExpiresAt, PlayerProfileResponse Player);
