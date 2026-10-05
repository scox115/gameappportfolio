using System.Net;
using System.Net.Http.Headers;

namespace Game.Client.Services;

// Attaches the signed-in player's access token to API calls, renewing it first when it is about
// to expire, and returns to the login screen when the API says the token is no longer valid.
public class AuthTokenHandler(GameState state, TokenRefresher refresher) : DelegatingHandler(new HttpClientHandler())
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await refresher.GetAccessTokenAsync();
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized && !string.IsNullOrEmpty(token))
        {
            state.SignOut(response.Headers.Contains(GameState.SessionEndedHeader)
                ? GameState.SignedInElsewhereMessage
                : "Your session expired. Please sign in again.");
        }

        return response;
    }
}
