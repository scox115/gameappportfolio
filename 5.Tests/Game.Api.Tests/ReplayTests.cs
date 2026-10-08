using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Game.Api.Hubs;
using Game.Api.Messaging;
using Game.Api.Models;
using Game.Core.Battles;
using Game.Core.Events;
using Game.Infrastructure.Data;
using Game.Infrastructure.History;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Game.Api.Tests;

// Every card of a duel is kept, so a finished duel can be played back (docs/adr/0038-match-replays.md).
public class ReplayTests
{
    private const string Password = "Arena-Pass1";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task AFinishedDuel_CanBePlayedBackCardByCard()
    {
        using var factory = new GameApiFactory();
        var (alice, bob, battle) = await StartDuelAsync(factory);
        var played = await FightToTheEndAsync(alice, bob, battle.Id);

        var visitor = factory.CreateClient(); // replays are public, like watching live
        var replay = (await visitor.GetFromJsonAsync<DuelReplayView>($"/api/v1/duels/{battle.Id}/replay", Json))!;

        Assert.Equal(played, replay.Moves.Count);
        Assert.Equal(Enumerable.Range(1, played), replay.Moves.Select(m => m.Turn));
        Assert.Equal(replay.PlayerOne.MaxHp, replay.PlayerOne.Hp); // as they started
        Assert.False(replay.PlayerOne.Shielded);
        Assert.Equal(PvpEndReason.Knockout, replay.EndReason);
        Assert.NotNull(replay.CompletedAt);

        // Each move's health follows from the one before.
        var (one, two) = (replay.PlayerOne.MaxHp, replay.PlayerTwo.MaxHp);
        foreach (var move in replay.Moves)
        {
            Assert.Equal("Fireball", move.CardName);
            if (move.PlayerId == replay.PlayerOne.Id) two -= move.DamageDealt; else one -= move.DamageDealt;
            Assert.Equal((Math.Max(0, one), Math.Max(0, two)), (move.PlayerOneHp, move.PlayerTwoHp));
        }
        var last = replay.Moves[^1];
        Assert.Equal(replay.WinnerId, last.PlayerId);
        Assert.Equal(0, Math.Min(last.PlayerOneHp, last.PlayerTwoHp));
    }

    [Fact]
    public async Task ADuelUnderWay_IsWatchedLive_NotReplayed()
    {
        using var factory = new GameApiFactory();
        var (_, _, battle) = await StartDuelAsync(factory);
        var visitor = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Conflict, (await visitor.GetAsync($"/api/v1/duels/{battle.Id}/replay")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync($"/api/v1/duels/{Guid.NewGuid()}/replay")).StatusCode);
    }

    [Fact]
    public async Task RecentMatches_LinkToTheDuelsReplay()
    {
        using var factory = new GameApiFactory();
        var (alice, bob, battle) = await StartDuelAsync(factory);
        await FightToTheEndAsync(alice, bob, battle.Id);
        await ProjectHistoryAsync(factory);

        var history = (await alice.Http.GetFromJsonAsync<List<MatchHistoryItemResponse>>("/api/v1/players/me/matches", Json))!;
        Assert.Equal(battle.Id, Assert.Single(history).ReplayId);
    }

    [Fact]
    public async Task DeletingAHero_DeletesTheirReplays()
    {
        using var factory = new GameApiFactory();
        var (alice, bob, battle) = await StartDuelAsync(factory);
        await FightToTheEndAsync(alice, bob, battle.Id);

        (await bob.Http.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/v1/players/me")
        {
            Content = JsonContent.Create(new { Password })
        })).EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<AppDbContext>().DuelMoves.AnyAsync(m => m.BattleId == battle.Id));
    }

    // Whoever's turn it is casts Fireball (which never recharges) until someone is knocked out. Returns the cards played.
    private static async Task<int> FightToTheEndAsync(Duelist alice, Duelist bob, Guid battleId)
    {
        var turn = alice.YourTurn ? alice : bob;
        for (var played = 1; played <= 100; played++)
        {
            await turn.Arena.InvokeAsync(nameof(ArenaHub.PlayCard), battleId, BattleCard.Fireball);
            var update = await turn.Updates.ReadAsync().AsTask().WaitAsync(Wait);
            if (update.Battle.Status == PvpBattleStatus.Finished) return played;
            turn = turn == alice ? bob : alice;
            await turn.Updates.ReadAsync().AsTask().WaitAsync(Wait); // the same move, from their side
        }
        throw new InvalidOperationException("The duel didn't end within 100 cards.");
    }

    private static async Task<(Duelist Alice, Duelist Bob, PvpBattleView Battle)> StartDuelAsync(GameApiFactory factory)
    {
        var alice = await JoinAsync(factory);
        var bob = await JoinAsync(factory);
        Assert.True(await alice.Arena.InvokeAsync<bool>(nameof(ArenaHub.FindOpponent)));
        Assert.False(await bob.Arena.InvokeAsync<bool>(nameof(ArenaHub.FindOpponent)));
        var battle = (await alice.MatchFound.ReadAsync().AsTask().WaitAsync(Wait)).Battle;
        await bob.MatchFound.ReadAsync().AsTask().WaitAsync(Wait);
        alice.YourTurn = battle.YourTurn;
        return (alice, bob, battle);
    }

    private static async Task ProjectHistoryAsync(GameApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var messages = await dbContext.OutboxMessages.ToListAsync();
        foreach (var message in messages)
        {
            var match = JsonSerializer.Deserialize<MatchCompletedEvent>(message.Payload, MatchEventJson.Options)!;
            await scope.ServiceProvider.GetRequiredService<MatchHistoryProjector>().ProjectAsync(match);
        }
    }

    private static async Task<Duelist> JoinAsync(GameApiFactory factory)
    {
        var http = factory.CreateClient();
        var username = $"replay{Guid.NewGuid():N}"[..16];
        var response = await http.PostAsJsonAsync("/api/v1/auth/register", new { Username = username, Password });
        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>(Json))!;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var arena = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, ArenaHub.Path), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(auth.AccessToken);
            })
            .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();
        var duelist = new Duelist(http, arena);
        await arena.StartAsync();
        return duelist;
    }

    private sealed class Duelist
    {
        private readonly Channel<PvpUpdate> _matchFound = Channel.CreateUnbounded<PvpUpdate>();
        private readonly Channel<PvpUpdate> _updates = Channel.CreateUnbounded<PvpUpdate>();

        public Duelist(HttpClient http, HubConnection arena)
        {
            Http = http;
            Arena = arena;
            arena.On<PvpUpdate>(nameof(IArenaClient.MatchFound), u => _matchFound.Writer.TryWrite(u));
            arena.On<PvpUpdate>(nameof(IArenaClient.BattleUpdated), u => _updates.Writer.TryWrite(u));
        }

        public HttpClient Http { get; }
        public HubConnection Arena { get; }
        public ChannelReader<PvpUpdate> MatchFound => _matchFound.Reader;
        public ChannelReader<PvpUpdate> Updates => _updates.Reader;
        public bool YourTurn { get; set; }
    }
}
