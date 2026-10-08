using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Game.Client.Models;

// The API's account, player profile and error shapes used by the sign-in screen, the town and the rankings.

public class PlayerProfileDto
{
    [JsonPropertyName("id")]
    public Guid id { get; set; }

    [JsonPropertyName("username")]
    public string username { get; set; } = "";

    [JsonPropertyName("gold")]
    public int gold { get; set; }

    [JsonPropertyName("level")]
    public int level { get; set; }

    [JsonPropertyName("class")]
    public string @class { get; set; } = "Sorcerer";

    // The leaderboard sends the class's display name.
    [JsonPropertyName("heroClass")]
    public string? heroClass { get; set; }

    [JsonPropertyName("avatarUrl")]
    public string? avatarUrl { get; set; }

    public string? title { get; set; }
    public int rating { get; set; }
    public int pvpWins { get; set; }
    public int pvpLosses { get; set; }
    public string? frame { get; set; }
    public string? cardSkin { get; set; }
    public bool isGuest { get; set; }
}

public class UploadResponse { public string avatarUrl { get; set; } = ""; }

public record PlayerStatsDto(int registeredPlayers);

public class AuthResponseDto
{
    public string accessToken { get; set; } = "";
    public DateTimeOffset expiresAt { get; set; }
    public string refreshToken { get; set; } = "";
    public PlayerProfileDto? player { get; set; }
    public List<string>? roles { get; set; }
}

public class ProblemDto
{
    public string? detail { get; set; }
    public bool twoFactorRequired { get; set; }
}

public class ValidationProblemDto
{
    public Dictionary<string, string[]>? errors { get; set; }

    // Turns an ASP.NET Core validation problem (e.g. password rules) into one readable line.
    public static async Task<string> ReadMessageAsync(HttpResponseMessage res)
    {
        try
        {
            var problem = await res.Content.ReadFromJsonAsync<ValidationProblemDto>();
            var messages = problem?.errors?.SelectMany(e => e.Value).ToList();
            if (messages is { Count: > 0 }) return string.Join(" ", messages);
        }
        catch (System.Text.Json.JsonException) { }
        return "Registration failed. Please check your details.";
    }
}
