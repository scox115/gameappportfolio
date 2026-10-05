using System.Net.Http.Json;

namespace Game.Client.Services;

/// <summary>
/// Keeps the player's access token fresh: shortly before it expires, trades the refresh token
/// for a new pair. Refreshes run one at a time, because each refresh token works only once.
/// </summary>
public class TokenRefresher(GameState state, ApiEndpoint api)
{
    // Renew a little early so a request doesn't leave with a token that expires on the way.
    private static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(1);

    // A plain client, so refresh calls don't loop back through AuthTokenHandler.
    private readonly HttpClient _http = new() { BaseAddress = api.BaseAddress };
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>A valid access token, renewed if needed; null when the player isn't signed in.</summary>
    public async Task<string?> GetAccessTokenAsync()
    {
        if (string.IsNullOrEmpty(state.AccessToken)) return null;
        if (!NeedsRenewal()) return state.AccessToken;

        await _gate.WaitAsync();
        try
        {
            // Another caller may have renewed it while this one waited.
            if (string.IsNullOrEmpty(state.AccessToken) || !NeedsRenewal()) return state.AccessToken;

            var response = await _http.PostAsJsonAsync("/api/auth/refresh", new { state.RefreshToken });
            var renewed = response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<RefreshResponse>() : null;
            if (renewed is null)
            {
                state.SignOut(response.Headers.Contains(GameState.SessionEndedHeader)
                    ? GameState.SignedInElsewhereMessage
                    : "Your session expired. Please sign in again.");
                return null;
            }

            state.UpdateTokens(new SessionTokens(renewed.AccessToken, renewed.ExpiresAt, renewed.RefreshToken));
            return renewed.AccessToken;
        }
        catch (HttpRequestException)
        {
            // The API is unreachable; keep the current token and let the request report the outage.
            return state.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Signs out, revoking the refresh token on the server so it can't be reused.</summary>
    public async Task SignOutAsync(string? reason = null)
    {
        var refreshToken = state.RefreshToken;
        state.SignOut(reason);

        if (string.IsNullOrEmpty(refreshToken)) return;
        try
        {
            await _http.PostAsJsonAsync("/api/auth/logout", new { RefreshToken = refreshToken });
        }
        catch (HttpRequestException)
        {
            // Already signed out locally; the token expires on its own.
        }
    }

    private bool NeedsRenewal() => state.AccessTokenExpiresAt - DateTimeOffset.UtcNow <= RenewBefore;

    private sealed record RefreshResponse(string AccessToken, DateTimeOffset ExpiresAt, string RefreshToken);
}
