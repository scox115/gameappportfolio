using System.Net.Http.Json;
using Game.Client.Models;
using Game.Client.Services;
using Microsoft.AspNetCore.Components;

namespace Game.Client.Shared;

// The leaderboard screen: every hero ranked by rating, or one class's duel records, reloaded live when
// the lobby feed says a match elsewhere changed them. Opens on the "all heroes" tab.
public partial class LeaderboardView : IDisposable
{
    [Inject] private GameState State { get; set; } = default!;
    [Inject] private HttpClient Http { get; set; } = default!;
    [Inject] private LiveLobby Lobby { get; set; } = default!;

    // null is every hero ranked by rating; a class shows only duels fought as that class.
    private static readonly string?[] LeaderboardTabs = { null, "Sorcerer", "Paladin", "Ranger" };
    private string? leaderboardClass;

    private bool leaderboardLoading = false;
    private List<PlayerProfileDto>? leaderboardPlayers;
    private int? registeredPlayers;
    // The latest match the shown rankings include, from the live lobby feed; a newer one reloads them.
    private DateTime? leaderboardMatchesUpTo;
    private bool leaderboardUpdatedLive;
    private string loadError = string.Empty;

    protected override async Task OnInitializedAsync()
    {
        Lobby.Changed += OnLobbyChanged;
        await ShowLeaderboard(null);
    }

    /// <summary>Starts again from the "all heroes" tab, for the header's Leaderboards button while the rankings are shown.</summary>
    public async Task ShowAllHeroesAsync()
    {
        var loading = ShowLeaderboard(null);
        StateHasChanged(); // show "loading" while it loads, as a tab click does
        await loading;
        StateHasChanged();
    }

    private void OnLobbyChanged() => _ = InvokeAsync(async () =>
    {
        var latest = Lobby.Current?.LeaderboardUpdatedAt;
        if (State.CurrentScreen != GameScreen.Leaderboard || leaderboardLoading || latest is null || latest == leaderboardMatchesUpTo) return;
        await ShowLeaderboard(leaderboardClass, quietly: true);
        leaderboardUpdatedLive = true;
        StateHasChanged();
    });

    // Quietly: a live refresh keeps the current table on screen until the new one arrives.
    private async Task ShowLeaderboard(string? heroClass, bool quietly = false)
    {
        leaderboardClass = heroClass;
        leaderboardUpdatedLive = false;
        leaderboardMatchesUpTo = Lobby.Current?.LeaderboardUpdatedAt;
        loadError = string.Empty;
        try
        {
            leaderboardLoading = !quietly;
            var stats = Http.GetFromJsonAsync<PlayerStatsDto>("/api/v1/players/stats");
            var players = await Http.GetFromJsonAsync<List<PlayerProfileDto>>(
                heroClass is null ? "/api/v1/players/leaderboard" : $"/api/v1/players/leaderboard?class={heroClass}");
            var registered = (await stats)?.registeredPlayers;
            if (leaderboardClass != heroClass) return; // Another tab was picked while this one loaded
            leaderboardPlayers = players;
            registeredPlayers = registered;
        }
        catch
        {
            loadError = "Failed to load leaderboard profiles from API framework.";
        }
        finally
        {
            leaderboardLoading = false;
        }
    }

    void IDisposable.Dispose()
    {
        Lobby.Changed -= OnLobbyChanged;
    }
}
