using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Game.Api.Hubs;
using Game.Api.Models;
using Game.Core.Battles;
using Game.Infrastructure.Data;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Game.Api.Tests;

// The live lobby: who is online, who is waiting for a duel, and duels under way, pushed to every
// open copy of the game (docs/adr/0034-live-lobby.md).
public class LiveLobbyTests
{
    private const string Password = "Arena-Pass1";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task AVisitor_SeesHeroesComeAndGo_AndDuelsStartAndEnd()
    {
        using var factory = new GameApiFactory();
        await using var visitor = await WatchLobbyAsync(factory); // not signed in
        await visitor.WaitForAsync(p => p is { PlayersOnline: 0, WaitingForDuel: 0, DuelsUnderWay: 0 });

        var alice = await SignUpAsync(factory);
        await using var aliceSession = await ConnectAsync(factory, SessionHub.Path, alice.Token);
        await visitor.WaitForAsync(p => p.PlayersOnline == 1);

        await using var aliceArena = await ConnectAsync(factory, ArenaHub.Path, alice.Token);
        Assert.True(await aliceArena.InvokeAsync<bool>(nameof(ArenaHub.FindOpponent)));
        await visitor.WaitForAsync(p => p is { PlayersOnline: 1, WaitingForDuel: 1 });

        var bob = await SignUpAsync(factory);
        await using var bobSession = await ConnectAsync(factory, SessionHub.Path, bob.Token);
        await using var bobArena = await ConnectAsync(factory, ArenaHub.Path, bob.Token);
        var matchFound = Channel.CreateUnbounded<PvpUpdate>();
        aliceArena.On<PvpUpdate>(nameof(IArenaClient.MatchFound), update => matchFound.Writer.TryWrite(update));
        Assert.False(await bobArena.InvokeAsync<bool>(nameof(ArenaHub.FindOpponent)));
        await visitor.WaitForAsync(p => p is { PlayersOnline: 2, WaitingForDuel: 0, DuelsUnderWay: 1 });

        var battle = (await matchFound.Reader.ReadAsync().AsTask().WaitAsync(Wait)).Battle;
        await aliceArena.InvokeAsync(nameof(ArenaHub.Forfeit), battle.Id);
        await visitor.WaitForAsync(p => p.DuelsUnderWay == 0);

        await aliceSession.StopAsync();
        await visitor.WaitForAsync(p => p.PlayersOnline == 1);
    }

    [Fact]
    public async Task TwoTabs_CountAsOneHero()
    {
        using var factory = new GameApiFactory();
        await using var visitor = await WatchLobbyAsync(factory);
        var alice = await SignUpAsync(factory);

        await using var firstTab = await ConnectAsync(factory, SessionHub.Path, alice.Token);
        await using var secondTab = await ConnectAsync(factory, SessionHub.Path, alice.Token);
        await visitor.WaitForAsync(p => p.PlayersOnline == 1);

        await firstTab.StopAsync();
        await Task.Delay(ArenaPulseWorkerTick * 2);
        Assert.Equal(1, visitor.Latest!.PlayersOnline);
    }

    [Fact]
    public async Task AFinishedMatch_TellsTheLobbyTheLeaderboardsMoved()
    {
        using var factory = new GameApiFactory();
        await using var visitor = await WatchLobbyAsync(factory);
        await visitor.WaitForAsync(p => p.LeaderboardUpdatedAt is null);

        var alice = await SignUpAsync(factory);
        await WinBossFightAsync(alice.Http);

        await visitor.WaitForAsync(p => p.LeaderboardUpdatedAt is not null);
    }

    [Fact]
    public async Task APlayerOnOneReplica_IsCountedOnTheOther()
    {
        var (east, west) = GameApiFactory.TwoReplicas();
        using (east)
        using (west)
        {
            await using var visitor = await WatchLobbyAsync(west);
            var alice = await SignUpAsync(east);

            await using var session = await ConnectAsync(east, SessionHub.Path, alice.Token);

            // East saw the change and broadcast it; the SQL backplane carried it to the visitor on West.
            await visitor.WaitForAsync(p => p.PlayersOnline == 1);
        }
    }

    [Fact]
    public async Task APlayerLeftByAStoppedReplica_IsNotCounted_AndIsClearedOut()
    {
        using var factory = new GameApiFactory();
        // A row no replica refreshes any more, as a replica leaves behind when it stops without saying goodbye.
        var longAgo = factory.Clock.GetUtcNow().UtcDateTime - OnlinePresence.StaleAfter * 2;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.OnlinePresence.Add(new OnlinePresence("gone-replica-connection", Guid.NewGuid(), longAgo));
            await db.SaveChangesAsync();
        }

        await using var visitor = await WatchLobbyAsync(factory);
        await visitor.WaitForAsync(p => p.PlayersOnline == 0);

        await factory.Services.GetRequiredService<ArenaPulse>().HeartbeatAsync();
        using (var scope = factory.Services.CreateScope())
        {
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>().OnlinePresence.ToListAsync());
        }
    }

    private static readonly TimeSpan ArenaPulseWorkerTick = Game.Api.Workers.ArenaPulseWorker.PublishInterval;

    private static async Task<LobbyWatcher> WatchLobbyAsync(GameApiFactory factory)
    {
        var connection = Build(factory, LobbyHub.Path, token: null);
        var watcher = new LobbyWatcher(connection);
        await connection.StartAsync();
        return watcher;
    }

    private static async Task<HubConnection> ConnectAsync(GameApiFactory factory, string path, string? token)
    {
        var connection = Build(factory, path, token);
        await connection.StartAsync();
        return connection;
    }

    private static HubConnection Build(GameApiFactory factory, string path, string? token) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, path), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                if (token is not null) options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();

    private static async Task<Hero> SignUpAsync(GameApiFactory factory)
    {
        var http = factory.CreateClient();
        var username = $"live{Guid.NewGuid():N}"[..16];
        var response = await http.PostAsJsonAsync("/api/v1/auth/register", new { Username = username, Password });
        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>(Json))!;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return new Hero(http, auth.AccessToken);
    }

    // FixedBattleRandom makes every card land, so alternating the two attacks always wins.
    private static async Task WinBossFightAsync(HttpClient client)
    {
        var start = await client.PostAsync("/api/v1/battles/pve", null);
        start.EnsureSuccessStatusCode();
        var battle = (await start.Content.ReadFromJsonAsync<BattleStateResponse>(Json))!;

        BattleCard? recharging = null;
        for (var turn = 0; turn < 50; turn++)
        {
            var card = recharging == BattleCard.DragonClaw ? "Fireball" : "DragonClaw";
            var response = await client.PostAsJsonAsync($"/api/v1/battles/pve/{battle.Id}/turns", new { Card = card });
            response.EnsureSuccessStatusCode();
            var result = (await response.Content.ReadFromJsonAsync<PlayCardResponse>(Json))!;
            if (result.Battle.Status != BattleStatus.InProgress) return;
            recharging = result.Battle.RechargingCard;
        }

        throw new InvalidOperationException("Battle did not finish within 50 turns.");
    }

    private sealed record Hero(HttpClient Http, string Token);

    private sealed class LobbyWatcher : IAsyncDisposable
    {
        private readonly HubConnection _connection;
        private readonly Channel<ArenaPulseView> _pulses = Channel.CreateUnbounded<ArenaPulseView>();

        public LobbyWatcher(HubConnection connection)
        {
            _connection = connection;
            connection.On<ArenaPulseView>(nameof(ILobbyClient.PulseUpdated), pulse =>
            {
                Latest = pulse;
                _pulses.Writer.TryWrite(pulse);
            });
        }

        public ArenaPulseView? Latest { get; private set; }

        /// <summary>Waits for a pulse that matches, skipping the ones on the way.</summary>
        public async Task WaitForAsync(Func<ArenaPulseView, bool> matches)
        {
            if (Latest is { } latest && matches(latest)) return;
            using var timeout = new CancellationTokenSource(Wait);
            try
            {
                await foreach (var pulse in _pulses.Reader.ReadAllAsync(timeout.Token))
                {
                    if (matches(pulse)) return;
                }
            }
            catch (OperationCanceledException)
            {
                Assert.Fail($"The lobby never showed the expected numbers; the last were {Latest}.");
            }
        }

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }
}
