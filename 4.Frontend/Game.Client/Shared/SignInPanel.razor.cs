using System.Net;
using System.Net.Http.Json;
using Game.Client.Models;
using Game.Client.Services;
using Microsoft.AspNetCore.Components;

namespace Game.Client.Shared;

// The sign-in screen: sign in (with the two-factor code step when the hero has it on), create a hero,
// or play as a guest. Each of them ends in StartSession, which signs the hero in and moves on to town.
public partial class SignInPanel : IDisposable
{
    [Inject] private GameState State { get; set; } = default!;
    [Inject] private HttpClient Http { get; set; } = default!;

    private string inputUsername = string.Empty;
    private string inputPassword = string.Empty;
    private string chosenClass = "Sorcerer";
    private bool creatingHero; // The sign-in screen is creating a new hero rather than signing in
    private bool startingGuest;

    private string inputTwoFactorCode = string.Empty;
    private bool askForTwoFactorCode; // The password was right and the hero has two-factor sign-in on
    private bool focusTwoFactorInput;
    private ElementReference twoFactorInput;

    private string statusMsg = string.Empty;

    protected override void OnInitialized()
    {
        // Shows why the last session ended (State.SignOutReason) as it changes.
        State.OnStateChanged += StateHasChanged;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (focusTwoFactorInput)
        {
            focusTwoFactorInput = false;
            await twoFactorInput.FocusAsync();
        }
    }

    private void ToggleCreatingHero()
    {
        creatingHero = !creatingHero;
        statusMsg = string.Empty;
    }

    // Play Game / Create Hero stay disabled until both fields have something in them.
    private bool CredentialsEntered => !string.IsNullOrWhiteSpace(inputUsername) && !string.IsNullOrEmpty(inputPassword);

    // Set while a sign-in or new hero is on its way to the server, so a second tap or Enter can't send
    // another: each sign-in ends the session before it, so the second one would sign the first out.
    private bool submitting;

    private async Task SubmitCredentials()
    {
        if (submitting) return;
        submitting = true;
        try { await (creatingHero ? HandleRegister() : HandleLogin()); }
        finally { submitting = false; }
    }

    private async Task HandleLogin()
    {
        if (string.IsNullOrWhiteSpace(inputUsername) || string.IsNullOrEmpty(inputPassword)) return;
        try
        {
            var res = await Http.PostAsJsonAsync("/api/v1/auth/login", new
            {
                Username = inputUsername,
                Password = inputPassword,
                TwoFactorCode = askForTwoFactorCode ? inputTwoFactorCode : null
            });
            if (res.IsSuccessStatusCode)
            {
                await StartSession(res);
            }
            else if (res.StatusCode == HttpStatusCode.Unauthorized && await ReadProblem(res) is { twoFactorRequired: true } problem)
            {
                // Show the code box; only say something's wrong if a code was already tried.
                statusMsg = askForTwoFactorCode ? problem.detail ?? "That code isn't right." : string.Empty;
                askForTwoFactorCode = focusTwoFactorInput = true;
                inputTwoFactorCode = string.Empty;
            }
            else if (res.StatusCode == HttpStatusCode.Forbidden)
            {
                // Suspended by an admin: the API says until when and why.
                statusMsg = await ReadProblemDetail(res) ?? "This hero is suspended.";
            }
            else statusMsg = res.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Wrong character name or password.",
                HttpStatusCode.Locked => "Too many failed attempts. Try again in a few minutes.",
                _ => "Sign-in failed. Please try again."
            };
        }
        catch { statusMsg = "API Network Link Offline."; }
    }

    private async Task HandleRegister()
    {
        if (string.IsNullOrWhiteSpace(inputUsername) || string.IsNullOrEmpty(inputPassword)) return;
        try
        {
            var res = await Http.PostAsJsonAsync("/api/v1/auth/register", new { Username = inputUsername, Password = inputPassword, Class = chosenClass });
            if (res.IsSuccessStatusCode)
            {
                // Registration signs the new hero straight in.
                await StartSession(res);
            }
            else if (res.StatusCode == HttpStatusCode.Conflict) statusMsg = "Username already claimed inside database.";
            else statusMsg = await ValidationProblemDto.ReadMessageAsync(res);
        }
        catch { statusMsg = "API Network Link Offline."; }
    }

    private async Task PlayAsGuest()
    {
        if (startingGuest) return;
        startingGuest = true;
        try
        {
            var res = await Http.PostAsJsonAsync("/api/v1/auth/guest", new { Class = chosenClass });
            if (res.IsSuccessStatusCode) await StartSession(res);
            else statusMsg = res.StatusCode == HttpStatusCode.TooManyRequests
                ? "Lots of guests have started from your network this hour. Create a hero instead, or try again later."
                : "Couldn't start a guest hero. Please try again.";
        }
        catch { statusMsg = "API Network Link Offline."; }
        finally { startingGuest = false; }
    }

    private async Task StartSession(HttpResponseMessage res)
    {
        var auth = await res.Content.ReadFromJsonAsync<AuthResponseDto>();
        if (auth?.player is null) return;

        statusMsg = string.Empty;
        inputPassword = inputTwoFactorCode = string.Empty;
        askForTwoFactorCode = false;
        creatingHero = false;
        State.UpdateClass(auth.player.@class);
        State.SetPlayerSession(new SessionTokens(auth.accessToken, auth.expiresAt, auth.refreshToken), auth.player.id, auth.player.username, auth.player.gold, auth.player.level, auth.player.avatarUrl);
        State.UpdateCosmetics(auth.player.frame, auth.player.cardSkin);
        State.UpdateGuest(auth.player.isGuest);
        State.UpdateRoles(auth.roles);
    }

    private static async Task<string?> ReadProblemDetail(HttpResponseMessage res) => (await ReadProblem(res))?.detail;

    private static async Task<ProblemDto?> ReadProblem(HttpResponseMessage res)
    {
        try
        {
            return await res.Content.ReadFromJsonAsync<ProblemDto>();
        }
        catch (System.Text.Json.JsonException) { return null; }
        catch (NotSupportedException) { return null; } // an empty or non-JSON body
    }

    void IDisposable.Dispose()
    {
        State.OnStateChanged -= StateHasChanged;
    }
}
