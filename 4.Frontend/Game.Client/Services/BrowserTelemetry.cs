using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;

namespace Game.Client.Services;

/// <summary>
/// Reports what players see to Application Insights through wwwroot/js/telemetry.js: a page view for
/// each game screen and page, and which player is signed in (by id). The script does nothing when
/// the deploy gave it no connection string, as in local runs. Errors are sent by TelemetryLoggerProvider.
/// </summary>
public sealed class BrowserTelemetry(IJSRuntime js, GameState state, NavigationManager navigation) : IDisposable
{
    private bool _started;
    private string? _view;
    private Guid _player;

    /// <summary>Starts reporting; called once, when the layout first renders.</summary>
    public async Task StartAsync()
    {
        if (_started) return;
        _started = true;

        await js.InvokeVoidAsync("gameTelemetry.start", AppVersion.Number.TrimStart('v'));
        state.OnStateChanged += OnStateChanged;
        navigation.LocationChanged += OnLocationChanged;
        await ReportAsync();
    }

    /// <summary>The name a view is reported under: the game's screen on the home page, or the page.</summary>
    public static string ViewName(string relativePath, GameScreen screen) =>
        relativePath.Split('?', '#')[0].Trim('/') switch
        {
            "" => screen switch
            {
                GameScreen.LoginMenu => "Sign in",
                GameScreen.CharacterDashboard => "Town",
                GameScreen.BattleArena => "Boss fight",
                GameScreen.PvpArena => "Duel",
                GameScreen.Shop => "Gold Shop",
                GameScreen.Leaderboard => "Leaderboard",
                _ => screen.ToString(),
            },
            "status" => "Status",
            "terms" => "Terms",
            "privacy" => "Privacy",
            "admin" => "Admin",
            _ => "Not found",
        };

    private void OnStateChanged() => _ = ReportAsync();

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e) => _ = ReportAsync();

    private async Task ReportAsync()
    {
        try
        {
            if (state.PlayerId != _player)
            {
                _player = state.PlayerId;
                await js.InvokeVoidAsync("gameTelemetry.setPlayer", _player == Guid.Empty ? null : _player.ToString());
            }

            var view = ViewName(navigation.ToBaseRelativePath(navigation.Uri), state.CurrentScreen);
            if (view != _view)
            {
                _view = view;
                await js.InvokeVoidAsync("gameTelemetry.trackView", view);
            }
        }
        catch (JSException)
        {
            // Telemetry must never get in the way of the game.
        }
    }

    public void Dispose()
    {
        state.OnStateChanged -= OnStateChanged;
        navigation.LocationChanged -= OnLocationChanged;
    }
}
