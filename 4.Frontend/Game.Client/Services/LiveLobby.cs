using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.JSInterop;

namespace Game.Client.Services;

/// <summary>What's happening in the arena right now (the API's ArenaPulseView).</summary>
public record LobbyPulse(int PlayersOnline, int WaitingForDuel, int DuelsUnderWay, DateTime? LeaderboardUpdatedAt);

/// <summary>
/// Listens to the API's live lobby feed, signed in or not, so the game can show how many heroes are
/// online and refresh the leaderboards when a match moves them (see docs/adr/0034-live-lobby.md).
/// </summary>
/// <remarks>
/// An open connection keeps an API replica (and its database) awake, so the feed pauses once nobody
/// has touched the page for the idle sign-out time, and picks up again on the next click or key.
/// </remarks>
public sealed class LiveLobby(ApiEndpoint api, IdleTimeoutSettings idle, IJSRuntime js) : IAsyncDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource _stop = new();
    private HubConnection? _hub;
    private bool _started;

    /// <summary>The latest numbers, or null while not connected.</summary>
    public LobbyPulse? Current { get; private set; }

    public event Action? Changed;

    /// <summary>Connects in the background, once; later calls do nothing.</summary>
    public void EnsureStarted()
    {
        if (_started) return;
        _started = true;
        _ = KeepConnectedAsync(_stop.Token);
    }

    private async Task KeepConnectedAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(CheckInterval);
        do
        {
            try
            {
                var active = TimeSpan.FromSeconds(await js.InvokeAsync<int>("gameIdle.secondsIdle", cancellationToken)) < idle.Timeout;
                if (active && _hub is null) await ConnectAsync(cancellationToken);
                else if (!active && _hub is not null) await DisconnectAsync();
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // The server may be waking up; the next check tries again.
                await DisconnectAsync();
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(api.BaseAddress, "hubs/lobby"), options =>
            {
                // Straight to WebSockets, as for the other hubs: with several API replicas, a separate
                // negotiate request could reach a different replica from the connection itself.
                options.SkipNegotiation = true;
                options.Transports = HttpTransportType.WebSockets;
            })
            .Build();
        hub.On<LobbyPulse>("PulseUpdated", pulse => Update(pulse));
        hub.Closed += _ =>
        {
            if (ReferenceEquals(_hub, hub)) _hub = null; // the next check reconnects
            Update(null);
            return Task.CompletedTask;
        };
        _hub = hub;
        await hub.StartAsync(cancellationToken);
    }

    private async Task DisconnectAsync()
    {
        var hub = _hub;
        _hub = null;
        if (hub is not null) await hub.DisposeAsync();
        Update(null);
    }

    private void Update(LobbyPulse? pulse)
    {
        Current = pulse;
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_hub is not null) await _hub.DisposeAsync();
    }
}
