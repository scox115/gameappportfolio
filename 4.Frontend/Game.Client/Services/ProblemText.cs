using System.Net.Http.Json;
using System.Text.Json;

namespace Game.Client.Services;

/// <summary>Reads the message out of an API error (RFC 7807 problem details), for showing to the player.</summary>
public static class ProblemText
{
    public static async Task<string?> ReadAsync(HttpResponseMessage response)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<Problem>();
            var details = problem?.errors?.SelectMany(e => e.Value).ToList();
            return details is { Count: > 0 } ? string.Join(" ", details) : problem?.detail;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null; // not JSON at all
        }
    }

    private sealed record Problem(string? detail, Dictionary<string, string[]>? errors);
}
