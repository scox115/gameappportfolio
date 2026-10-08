using Game.Client.Services;
using Microsoft.AspNetCore.Components;

namespace Game.Client.Shared;

// The header bar: the game's name and version, and once signed in the hero's status pill with the
// Town, Leaderboards, sound and Logout buttons.
public partial class GameHeader : IDisposable
{
    [Inject] private GameState State { get; set; } = default!;
    [Inject] private TokenRefresher Tokens { get; set; } = default!;
    [Inject] private SoundEffects Sounds { get; set; } = default!;

    /// <summary>The Leaderboards button: the page switches to the rankings (or reloads them).</summary>
    [Parameter] public EventCallback OnShowLeaderboards { get; set; }

    protected override void OnInitialized()
    {
        State.OnStateChanged += StateHasChanged;
        Sounds.OnChanged += StateHasChanged;
    }

    private async Task Logout()
    {
        await Tokens.SignOutAsync();
    }

    void IDisposable.Dispose()
    {
        State.OnStateChanged -= StateHasChanged;
        Sounds.OnChanged -= StateHasChanged;
    }
}
