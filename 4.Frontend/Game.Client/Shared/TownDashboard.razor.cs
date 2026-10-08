using Game.Client.Models;
using Game.Client.Services;
using Microsoft.AspNetCore.Components;

namespace Game.Client.Shared;

// The town screen: the hero's card and its buttons (boss battles, duels, the Gold Shop, account
// settings, admin tools), the portrait popup, and the season, friends, bounty and match panels.
public partial class TownDashboard : IDisposable
{
    [Inject] private GameState State { get; set; } = default!;
    [Inject] private HttpClient Http { get; set; } = default!;

    /// <summary>A boss battle was started (or resumed); the page shows it.</summary>
    [Parameter] public EventCallback<BossBattleStart> OnBattleStarted { get; set; }

    private string statusMsg = string.Empty; // Why the arena didn't open
    private bool showPortraitPopup;

    protected override void OnInitialized()
    {
        // The card shows the hero's name, portrait and frame, which change while in town.
        State.OnStateChanged += StateHasChanged;
    }

    private void OpenPortraitPopup() => showPortraitPopup = true;

    private void ClosePortraitPopup() => showPortraitPopup = false;

    private async Task EnterArena(bool heroic = false)
    {
        var (start, error) = await BossBattle.StartAsync(Http, heroic);
        statusMsg = error ?? string.Empty;
        if (start is not null) await OnBattleStarted.InvokeAsync(start);
    }

    void IDisposable.Dispose()
    {
        State.OnStateChanged -= StateHasChanged;
    }
}
