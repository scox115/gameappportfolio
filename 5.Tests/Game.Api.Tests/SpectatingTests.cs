using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Game.Api.Hubs;
using Game.Api.Models;
using Game.Core.Battles;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.SignalR.Client;

namespace Game.Api.Tests;

// Anyone, signed in or not, can list the duels under way and watch one turn by turn (docs/adr/0036-spectating.md).
public class SpectatingTests
{
    private const string Password = "Arena-Pass1";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task AVisitor_FindsADuelUnderWay_AndWatchesItTurnByTurn()
    {
        using var factory = new GameApiFactory();
        var (alice, bob, battle) = await StartDuelAsync(factory, factory);

        var live = await factory.CreateClient().GetFromJsonAsync<List<DuelWatchView>>("/api/v1/duels/live", Json);
        var listed = Assert.Single(live!);
        Assert.Equal(battle.Id, listed.Id);
        Assert.Equal(new[] { alice.Username, bob.Username }.Order(), new[] { listed.PlayerOne.Username, listed.PlayerTwo.Username }.Order());

        await using var visitor = await WatchAsync(factory); // not signed in
        var watched = await visitor.Hub.InvokeAsync<DuelWatchView?>(nameof(LobbyHub.WatchDuel), battle.Id);
        Assert.NotNull(watched);
        Assert.Equal(PvpBattleStatus.InProgress, watched.Status);
        Assert.Equal(1, watched.Turn);

        var (mover, waiter) = battle.YourTurn ? (alice, bob) : (bob, alice);
        Assert.Equal(mover.Id, watched.ActivePlayerId);
        await mover.Arena.InvokeAsync(nameof(ArenaHub.PlayCard), battle.Id, BattleCard.Fireball);

        var move = await visitor.NextAsync();
        Assert.Equal(mover.Username, move.LastTurn!.PlayerName);
        Assert.Equal("Fireball", move.LastTurn.CardName);
        Assert.False(move.LastTurn.YourCard);
        Assert.Equal(waiter.Id, move.Duel.ActivePlayerId);
        var target = move.Duel.PlayerOne.Id == waiter.Id ? move.Duel.PlayerOne : move.Duel.PlayerTwo;
        Assert.Equal(target.MaxHp - move.LastTurn.DamageDealt, target.Hp);

        await waiter.Arena.InvokeAsync(nameof(ArenaHub.Forfeit), battle.Id);
        var end = await visitor.NextAsync();
        Assert.Equal(PvpBattleStatus.Finished, end.Duel.Status);
        Assert.Equal(PvpEndReason.Forfeit, end.Duel.EndReason);
        Assert.Equal(mover.Id, end.Duel.WinnerId);
        Assert.Null(end.Duel.ActivePlayerId);
    }

    [Fact]
    public async Task ASpectatorWhoStopsWatching_HearsNoMore()
    {
        using var factory = new GameApiFactory();
        var (alice, bob, battle) = await StartDuelAsync(factory, factory);
        await using var visitor = await WatchAsync(factory);
        await visitor.Hub.InvokeAsync<DuelWatchView?>(nameof(LobbyHub.WatchDuel), battle.Id);

        await visitor.Hub.InvokeAsync(nameof(LobbyHub.StopWatching), battle.Id);
        await alice.Arena.InvokeAsync(nameof(ArenaHub.Forfeit), battle.Id);

        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.False(visitor.Updates.TryRead(out _));
    }

    [Fact]
    public async Task WatchingADuelThatDoesNotExist_ReturnsNothing()
    {
        using var factory = new GameApiFactory();
        await using var visitor = await WatchAsync(factory);

        Assert.Null(await visitor.Hub.InvokeAsync<DuelWatchView?>(nameof(LobbyHub.WatchDuel), Guid.NewGuid()));
    }

    [Fact]
    public async Task AFinishedDuel_IsShownWithItsResult_AndLeavesTheList()
    {
        using var factory = new GameApiFactory();
        var (alice, bob, battle) = await StartDuelAsync(factory, factory);
        await alice.Arena.InvokeAsync(nameof(ArenaHub.Forfeit), battle.Id);
        await Task.Delay(TimeSpan.FromSeconds(2.5)); // past the list's short cache

        Assert.Empty((await factory.CreateClient().GetFromJsonAsync<List<DuelWatchView>>("/api/v1/duels/live", Json))!);
        await using var visitor = await WatchAsync(factory);
        var watched = await visitor.Hub.InvokeAsync<DuelWatchView?>(nameof(LobbyHub.WatchDuel), battle.Id);
        Assert.Equal(bob.Id, watched!.WinnerId);
    }

    [Fact]
    public async Task ASpectatorOnAnotherReplica_SeesTheMoves()
    {
        var (east, west) = GameApiFactory.TwoReplicas();
        using (east)
        using (west)
        {
            var (alice, bob, battle) = await StartDuelAsync(east, east);
            await using var visitor = await WatchAsync(west);
            await visitor.Hub.InvokeAsync<DuelWatchView?>(nameof(LobbyHub.WatchDuel), battle.Id);

            // East plays the move and tells its own spectators (none); the SQL backplane carries it to West's.
            await alice.Arena.InvokeAsync(nameof(ArenaHub.Forfeit), battle.Id);

            var end = await visitor.NextAsync();
            Assert.Equal(bob.Id, end.Duel.WinnerId);
        }
    }

    private static async Task<(Duelist Alice, Duelist Bob, PvpBattleView Battle)> StartDuelAsync(GameApiFactory aliceReplica, GameApiFactory bobReplica)
    {
        var alice = await JoinAsync(aliceReplica);
        var bob = await JoinAsync(bobReplica);
        var matchFound = Channel.CreateUnbounded<PvpUpdate>();
        alice.Arena.On<PvpUpdate>(nameof(IArenaClient.MatchFound), update => matchFound.Writer.TryWrite(update));

        Assert.True(await alice.Arena.InvokeAsync<bool>(nameof(ArenaHub.FindOpponent)));
        Assert.False(await bob.Arena.InvokeAsync<bool>(nameof(ArenaHub.FindOpponent)));
        var battle = (await matchFound.Reader.ReadAsync().AsTask().WaitAsync(Wait)).Battle;
        return (alice, bob, battle);
    }

    private static async Task<Duelist> JoinAsync(GameApiFactory factory)
    {
        var http = factory.CreateClient();
        var username = $"seen{Guid.NewGuid():N}"[..16];
        var response = await http.PostAsJsonAsync("/api/v1/auth/register", new { Username = username, Password });
        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>(Json))!;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        var me = (await http.GetFromJsonAsync<JsonElement>("/api/v1/players/me", Json)).GetProperty("id").GetGuid();

        var arena = Build(factory, ArenaHub.Path, auth.AccessToken);
        await arena.StartAsync();
        return new Duelist(me, username, arena);
    }

    private static async Task<Spectator> WatchAsync(GameApiFactory factory)
    {
        var hub = Build(factory, LobbyHub.Path, token: null);
        var spectator = new Spectator(hub);
        await hub.StartAsync();
        return spectator;
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

    private sealed record Duelist(Guid Id, string Username, HubConnection Arena);

    private sealed class Spectator : IAsyncDisposable
    {
        private readonly Channel<DuelWatchUpdate> _updates = Channel.CreateUnbounded<DuelWatchUpdate>();

        public Spectator(HubConnection hub)
        {
            Hub = hub;
            hub.On<DuelWatchUpdate>(nameof(ILobbyClient.DuelUpdated), update => _updates.Writer.TryWrite(update));
        }

        public HubConnection Hub { get; }

        public ChannelReader<DuelWatchUpdate> Updates => _updates.Reader;

        public async Task<DuelWatchUpdate> NextAsync() => await _updates.Reader.ReadAsync().AsTask().WaitAsync(Wait);

        public ValueTask DisposeAsync() => Hub.DisposeAsync();
    }
}
