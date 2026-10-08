using System.Net.Http.Json;
using Game.Client.Models;
using Game.Client.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace Game.Client.Pages;

// The live PvP arena: owns the SignalR connection, the duel state and every server call.
// The lobby, the heroes' panels and the result panel are Shared/DuelLobby, DuelPlayerPanel and DuelResult.
public partial class PvpArena : IAsyncDisposable
{
    [Inject] private GameState State { get; set; } = default!;
    [Inject] private ApiEndpoint Api { get; set; } = default!;
    [Inject] private TokenRefresher Tokens { get; set; } = default!;
    [Inject] private SoundEffects Sounds { get; set; } = default!;
    [Inject] private HttpClient Http { get; set; } = default!;

    private HubConnection? hub;
    private PvpBattleDto? battle;
    private PvpRewardDto? reward;
    private List<string> logs = new();
    private bool searching;
    private bool sending;
    private string? errorMsg;
    private bool picking;
    private int stake;
    private int secondsLeft;
    private Timer? countdown;
    private ChallengeSentDto? challenge; // sent to a friend, waiting for their answer
    private int challengeSecondsLeft;

    protected override async Task OnInitializedAsync()
    {
        hub = new HubConnectionBuilder()
            .WithUrl(new Uri(Api.BaseAddress, "hubs/arena"), options =>
            {
                options.AccessTokenProvider = Tokens.GetAccessTokenAsync;
                // Straight to WebSockets: with several API replicas, a separate negotiate request could reach a
                // different replica from the connection itself.
                options.SkipNegotiation = true;
                options.Transports = HttpTransportType.WebSockets;
            })
            .WithAutomaticReconnect()
            .Build();

        hub.On<PvpUpdateDto>("MatchFound", update => InvokeAsync(() => OnMatchFound(update)));
        hub.On<PvpUpdateDto>("BattleUpdated", update => InvokeAsync(() => OnBattleUpdated(update)));
        hub.On<string>("ChallengeDeclined", reason => InvokeAsync(() =>
        {
            challenge = null;
            picking = battle is null;
            errorMsg = reason;
            StateHasChanged();
        }));
        hub.On<string>("SearchCancelled", reason => InvokeAsync(() =>
        {
            searching = false;
            picking = true;
            errorMsg = reason;
            StateHasChanged();
        }));

        // After a dropped connection comes back, rejoin the lobby or pick up an unfinished battle.
        // A challenge is taken back when its connection drops, so the player chooses again.
        hub.Reconnected += _ => InvokeAsync(() =>
        {
            if (challenge is not null)
            {
                challenge = null;
                errorMsg = "The connection dropped, so your challenge was taken back. Send it again from town.";
            }
            return searching ? Search(stake) : ResumeOrPick();
        });
        State.OnStateChanged += OnStateChanged;

        countdown = new Timer(_ => InvokeAsync(Tick), null, 1000, 1000);

        try
        {
            await hub.StartAsync();
            if (!await AnswerFriendsAsync()) await ResumeOrPick();
        }
        catch (Exception ex)
        {
            errorMsg = $"Could not reach the arena: {ex.Message}";
        }
    }

    // An unfinished battle carries on; otherwise the player chooses a wager.
    private async Task ResumeOrPick()
    {
        try
        {
            var response = await Http.GetAsync("/api/v1/battles/pvp/current");
            if (response.StatusCode == System.Net.HttpStatusCode.OK
                && await response.Content.ReadFromJsonAsync<PvpBattleDto>(JsonOptions) is { } current)
            {
                // Rejoining the lobby hands the battle back over the hub, with the live turn timer.
                await Search(current.Wager);
                return;
            }
        }
        catch (Exception)
        {
            // Fall back to the wager picker.
        }
        picking = battle is null;
        StateHasChanged();
    }

    private async Task Search(int wager)
    {
        if (hub is null) return;
        try
        {
            errorMsg = null;
            stake = wager;
            searching = wager == 0
                ? await hub.InvokeAsync<bool>("FindOpponent")
                : await hub.InvokeAsync<bool>("FindWageredOpponent", wager);
            picking = false;
        }
        catch (Exception ex)
        {
            errorMsg = ex.Message.Contains("HubException: ") ? ex.Message[(ex.Message.IndexOf("HubException: ") + 14)..] : ex.Message;
            searching = false;
            picking = true;
        }
        StateHasChanged();
    }

    // Leaves the lobby for a practice duel the server plays against you; MatchFound brings the battle.
    private async Task DuelBot()
    {
        if (hub is null) return;
        try
        {
            errorMsg = null;
            await hub.InvokeAsync("DuelBot");
            searching = false;
            picking = false;
        }
        catch (Exception ex)
        {
            errorMsg = ex.Message.Contains("HubException: ") ? ex.Message[(ex.Message.IndexOf("HubException: ") + 14)..] : ex.Message;
        }
        StateHasChanged();
    }

    // A challenge accepted, or a friend picked to challenge, from another screen (see docs/adr/0037-friends-and-challenges.md).
    // Returns false when there was neither.
    private async Task<bool> AnswerFriendsAsync()
    {
        if (hub?.State != HubConnectionState.Connected) return false;
        if (State.TakeChallengeToAccept() is { } accepted)
        {
            await AcceptChallenge(accepted);
            return true;
        }
        if (State.TakeFriendToChallenge() is { } friend)
        {
            await ChallengeFriend(friend);
            return true;
        }
        return false;
    }

    // Accepting an invite while already on this screen.
    private void OnStateChanged()
    {
        if (State.ChallengeToAccept is not null || State.FriendToChallenge is not null) _ = InvokeAsync(AnswerFriendsAsync);
    }

    private async Task AcceptChallenge(Guid challengeId)
    {
        if (hub is null) return;
        try
        {
            errorMsg = null;
            if (searching) await hub.InvokeAsync("CancelSearch");
            searching = false;
            picking = false;
            await hub.InvokeAsync("AcceptChallenge", challengeId); // MatchFound brings the duel
        }
        catch (Exception ex)
        {
            errorMsg = HubError(ex);
            picking = battle is null;
        }
        StateHasChanged();
    }

    private async Task ChallengeFriend(ChallengeTarget friend)
    {
        if (hub is null) return;
        try
        {
            errorMsg = null;
            searching = false;
            picking = false;
            challenge = await hub.InvokeAsync<ChallengeSentDto?>("ChallengeFriend", friend.Id);
            challengeSecondsLeft = challenge?.SecondsLeft ?? 0;
        }
        catch (Exception ex)
        {
            errorMsg = HubError(ex);
            picking = battle is null;
        }
        StateHasChanged();
    }

    private async Task WithdrawChallenge()
    {
        challenge = null;
        picking = true;
        if (hub is not null)
        {
            try { await hub.InvokeAsync("WithdrawChallenge"); } catch { /* it runs out anyway */ }
        }
    }

    private static string HubError(Exception ex) =>
        ex.Message.Contains("HubException: ") ? ex.Message[(ex.Message.IndexOf("HubException: ") + 14)..] : ex.Message;

    private async Task CancelSearch()
    {
        if (hub is not null)
        {
            try { await hub.InvokeAsync("CancelSearch"); } catch { /* back to the picker anyway */ }
        }
        searching = false;
        picking = true;
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web);

    private void OnMatchFound(PvpUpdateDto update)
    {
        searching = false;
        challenge = null;
        reward = null;
        logs = new() { $"⚔️ Battle on! You face {update.Battle.Opponent.Username}." };
        _ = Sounds.PlayAsync(update.Battle.YourTurn ? Sound.YourTurn : Sound.MatchFound);
        Apply(update);
        if (update.Battle.Wager > 0) _ = RefreshGoldAsync();
    }

    // The server takes a wager's stake when the duel starts, so show the new balance.
    private async Task RefreshGoldAsync()
    {
        try
        {
            if (await Http.GetFromJsonAsync<PvpProfileDto>("/api/v1/players/me", JsonOptions) is { } me)
            {
                State.UpdateGold(me.Gold);
            }
        }
        catch (Exception)
        {
            // The header catches up after the duel's rewards.
        }
    }

    private void OnBattleUpdated(PvpUpdateDto update)
    {
        if (battle is not null && update.Battle.Id != battle.Id) return;

        if (update.LastTurn is { } turn)
        {
            var who = turn.YourCard ? "You" : turn.PlayerName;
            _ = Sounds.PlayAsync(turn switch
            {
                { CardFailed: true } => Sound.Miss,
                { AttackBlocked: true } => Sound.Block,
                { DamageDealt: > 0, YourCard: false } => Sound.Hit,
                _ => SoundEffects.ForCard(turn.CardName)
            });
            if (turn.CardFailed)
                logs.Add($"🌀 Turn {turn.Turn}: {who} played {turn.CardName}, but it was {turn.CardFailedReason}!");
            else if (turn.AttackBlocked)
                logs.Add($"🛡️ Turn {turn.Turn}: {who} played {turn.CardName}, but it was blocked by a shield!");
            else if (turn.DamageDealt > 0)
                logs.Add($"» Turn {turn.Turn}: {who} cast {turn.CardName} for {turn.DamageDealt} DMG.");
            else
                logs.Add($"» Turn {turn.Turn}: {who} raised {turn.CardName}, healing {turn.HealthRestored} HP.");
        }

        if (update.Battle.Status != "InProgress")
        {
            _ = Sounds.PlayAsync(update.Battle.YouWon == true ? Sound.Victory : Sound.Defeat);
            logs.Add(update.Battle.EndReason switch
            {
                "Timeout" => update.Battle.YouWon == true ? "⌛ Your opponent ran out of time." : "⌛ You ran out of time.",
                "Forfeit" => update.Battle.YouWon == true ? "🏳️ Your opponent forfeited." : "🏳️ You forfeited.",
                _ => update.Battle.YouWon == true ? "🏆 Knockout! You win." : "💀 You were knocked out."
            });
        }

        Apply(update);
    }

    private void Apply(PvpUpdateDto update)
    {
        battle = update.Battle;
        secondsLeft = update.Battle.TurnSecondsLeft;
        if (update.Reward is { } earned)
        {
            reward = earned;
            State.UpdateRewards(earned.Player.Gold, earned.Player.Level);
        }
        StateHasChanged();
    }

    private void Tick()
    {
        if (challenge is { } sent && --challengeSecondsLeft <= 0)
        {
            errorMsg = $"{sent.FriendName} didn't answer in time.";
            _ = WithdrawChallenge();
            StateHasChanged();
        }
        if (battle?.Status != "InProgress" || secondsLeft <= 0) return;
        secondsLeft--;
        StateHasChanged();
    }

    private async Task PlayCard(string card)
    {
        if (hub is null || battle is null || sending) return;
        sending = true;
        try
        {
            await hub.InvokeAsync("PlayCard", battle.Id, card);
        }
        catch (Exception ex)
        {
            logs.Add($"[Move rejected] {ex.Message}");
        }
        finally
        {
            sending = false;
        }
    }

    private async Task Forfeit()
    {
        if (hub is null || battle is null) return;
        try
        {
            await hub.InvokeAsync("Forfeit", battle.Id);
        }
        catch (Exception ex)
        {
            logs.Add($"[Forfeit failed] {ex.Message}");
        }
    }

    private async Task SparAgain()
    {
        battle = null;
        reward = null;
        logs.Clear();
        await DuelBot();
    }

    private async Task FindAnother()
    {
        battle = null;
        reward = null;
        logs.Clear();
        if (stake <= State.Gold && (stake == 0 || !State.IsGuest))
        {
            await Search(stake);
        }
        else
        {
            picking = true;
        }
    }

    private async Task LeaveLobby()
    {
        if (challenge is not null) await WithdrawChallenge();
        if (hub is not null && searching)
        {
            try { await hub.InvokeAsync("CancelSearch"); } catch { /* leaving anyway */ }
        }
        State.ChangeScreen(GameScreen.CharacterDashboard);
    }

    public async ValueTask DisposeAsync()
    {
        State.OnStateChanged -= OnStateChanged;
        countdown?.Dispose();
        if (hub is not null)
        {
            await hub.DisposeAsync();
        }
    }
}
