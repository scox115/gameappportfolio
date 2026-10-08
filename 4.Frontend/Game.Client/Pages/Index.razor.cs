using Game.Client.Models;
using Game.Client.Services;
using Game.Client.Shared;
using Microsoft.AspNetCore.Components;

namespace Game.Client.Pages;

// The game's one page: the header and whichever screen GameState says the player is on.
public partial class Index : IDisposable
{
    [Inject] private GameState State { get; set; } = default!;
    [Inject] private SoundEffects Sounds { get; set; } = default!;

    // The boss battle to show. Starting one is a town (or "Fight Again") action whose result the battle
    // screen shows, so the starter hands it up here through an EventCallback and this page passes it down
    // as BossBattle's parameter. That keeps it next to the screen switch that renders the battle, rather
    // than adding a field to the app-wide GameState that only these two screens use.
    private BossBattleStart? bossBattle;

    private LeaderboardView? leaderboard;

    protected override void OnInitialized()
    {
        State.OnStateChanged += StateHasChanged;
    }

    // The saved sound setting lives in the browser, which is only reachable after the first render.
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender) await Sounds.LoadAsync();
    }

    private void StartBossBattle(BossBattleStart start)
    {
        bossBattle = start;
        State.ChangeScreen(GameScreen.BattleArena);
    }

    // LeaderboardView loads the "all heroes" tab when it first appears; when the rankings are already
    // on screen, the header button starts them again from that tab.
    private async Task ShowLeaderboards()
    {
        var alreadyShown = State.CurrentScreen == GameScreen.Leaderboard;
        State.ChangeScreen(GameScreen.Leaderboard);
        if (alreadyShown && leaderboard is not null) await leaderboard.ShowAllHeroesAsync();
    }

    void IDisposable.Dispose()
    {
        State.OnStateChanged -= StateHasChanged;
    }
}
