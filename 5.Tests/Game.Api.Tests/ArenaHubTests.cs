using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Game.Api.Hubs;
using Game.Api.Models;
using Game.Core.Battles;
using Game.Core.Services;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Game.Api.Tests;

// Two real SignalR clients play against each other through the in-memory test server.
// FixedBattleRandom makes every card land, and the coin flip always lets the second player to
// join the lobby go first.
public class ArenaHubTests : IClassFixture<GameApiFactory>
{
    private const string Password = "Arena-Pass1";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly GameApiFactory _factory;

    public ArenaHubTests(GameApiFactory factory) => _factory = factory;

    [Fact]
    public async Task TwoPlayersInTheLobbyArePairedIntoTheSameBattle()
    {
        await using var alice = await ConnectAsync();
        await using var bob = await ConnectAsync();

        Assert.True(await alice.FindOpponentAsync());
        Assert.False(await bob.FindOpponentAsync());

        var aliceView = (await alice.MatchFound.ReadAsync()).Battle;
        var bobView = (await bob.MatchFound.ReadAsync()).Battle;
        Assert.Equal(aliceView.Id, bobView.Id);
        Assert.Equal(bob.Username, aliceView.Opponent.Username);
        Assert.Equal(alice.Username, bobView.Opponent.Username);
        Assert.True(bobView.YourTurn);
        Assert.False(aliceView.YourTurn);
    }

    [Fact]
    public async Task PlayingACard_UpdatesBothPlayers()
    {
        var (alice, bob, battleId) = await StartBattleAsync();
        await using (alice)
        await using (bob)
        {
            await bob.PlayCardAsync(battleId, BattleCard.Fireball);

            var bobUpdate = await bob.Updates.ReadAsync();
            var aliceUpdate = await alice.Updates.ReadAsync();
            Assert.True(bobUpdate.LastTurn!.YourCard);
            Assert.False(aliceUpdate.LastTurn!.YourCard);
            Assert.Equal(20, aliceUpdate.LastTurn.DamageDealt);
            Assert.Equal(80, aliceUpdate.Battle.You.Hp);
            Assert.Equal(80, bobUpdate.Battle.Opponent.Hp);
            Assert.True(aliceUpdate.Battle.YourTurn);
        }
    }

    [Fact]
    public async Task PlayingOutOfTurn_IsRejected()
    {
        var (alice, bob, battleId) = await StartBattleAsync();
        await using (alice)
        await using (bob)
        {
            var error = await Assert.ThrowsAsync<HubException>(() => alice.PlayCardAsync(battleId, BattleCard.Fireball));
            Assert.Contains("not your turn", error.Message);
        }
    }

    [Fact]
    public async Task APowerCardCantBePlayedTwiceInARow()
    {
        var (alice, bob, battleId) = await StartBattleAsync();
        await using (alice)
        await using (bob)
        {
            await bob.PlayCardAsync(battleId, BattleCard.DragonClaw);
            await alice.PlayCardAsync(battleId, BattleCard.Fireball);

            var error = await Assert.ThrowsAsync<HubException>(() => bob.PlayCardAsync(battleId, BattleCard.DragonClaw));
            Assert.Contains("recharging", error.Message);
        }
    }

    [Fact]
    public async Task Knockout_EndsTheBattleAndPaysBothPlayers()
    {
        var (alice, bob, battleId) = await StartBattleAsync();
        await using (alice)
        await using (bob)
        {
            // Bob goes first, so trading Dragon Claw and Fireball he lands the final blow.
            PvpUpdate aliceUpdate;
            PvpUpdate bobUpdate;
            var turn = 0;
            do
            {
                var mover = turn % 2 == 0 ? bob : alice;
                await mover.PlayCardAsync(battleId, turn % 4 < 2 ? BattleCard.DragonClaw : BattleCard.Fireball);
                aliceUpdate = await alice.Updates.ReadAsync();
                bobUpdate = await bob.Updates.ReadAsync();
                turn++;
            } while (bobUpdate.Battle.Status == PvpBattleStatus.InProgress);

            Assert.True(bobUpdate.Battle.YouWon);
            Assert.False(aliceUpdate.Battle.YouWon);
            Assert.Equal(PvpEndReason.Knockout, bobUpdate.Battle.EndReason);
            Assert.Equal(MatchRulesEngine.WinGold, bobUpdate.Reward!.GoldEarned);
            Assert.Equal(MatchRulesEngine.LossGold, aliceUpdate.Reward!.GoldEarned);

            var bobProfile = await bob.Http.GetFromJsonAsync<PlayerProfileResponse>("/api/players/me", Json);
            Assert.Equal(500 + MatchRulesEngine.WinGold, bobProfile!.Gold);

            var current = await bob.Http.GetAsync("/api/battles/pvp/current");
            Assert.Equal(HttpStatusCode.NoContent, current.StatusCode);
        }
    }

    [Fact]
    public async Task Forfeiting_GivesTheOpponentTheWin()
    {
        var (alice, bob, battleId) = await StartBattleAsync();
        await using (alice)
        await using (bob)
        {
            await alice.ForfeitAsync(battleId);

            var bobUpdate = await bob.Updates.ReadAsync();
            var aliceUpdate = await alice.Updates.ReadAsync();
            Assert.True(bobUpdate.Battle.YouWon);
            Assert.False(aliceUpdate.Battle.YouWon);
            Assert.Equal(PvpEndReason.Forfeit, aliceUpdate.Battle.EndReason);
        }
    }

    [Fact]
    public async Task RunningOutOfTime_LosesTheBattle()
    {
        var (alice, bob, battleId) = await StartBattleAsync();
        await using (alice)
        await using (bob)
        {
            _factory.Clock.Advance(PvpBattle.TurnTimeLimit);
            using (var scope = _factory.Services.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<PvpBattleService>().ExpireOverdueTurnsAsync();
            }

            var bobUpdate = await bob.Updates.ReadAsync();
            var aliceUpdate = await alice.Updates.ReadAsync();
            Assert.False(bobUpdate.Battle.YouWon);
            Assert.True(aliceUpdate.Battle.YouWon);
            Assert.Equal(PvpEndReason.Timeout, bobUpdate.Battle.EndReason);

            var lateMove = await Assert.ThrowsAsync<HubException>(() => bob.PlayCardAsync(battleId, BattleCard.Fireball));
            Assert.Contains("over", lateMove.Message);
        }
    }

    [Fact]
    public async Task ReturningToTheLobby_ResumesTheUnfinishedBattle()
    {
        var (alice, bob, battleId) = await StartBattleAsync();
        await using (bob)
        {
            await alice.DisposeAsync();

            var current = await alice.Http.GetFromJsonAsync<PvpBattleView>("/api/battles/pvp/current", Json);
            Assert.Equal(battleId, current!.Id);

            await using var aliceAgain = await ConnectAsync(alice.Http, alice.Token, alice.Username);
            Assert.False(await aliceAgain.FindOpponentAsync());
            Assert.Equal(battleId, (await aliceAgain.MatchFound.ReadAsync()).Battle.Id);

            await bob.ForfeitAsync(battleId);
        }
    }

    [Fact]
    public async Task TheHubRequiresSignIn()
    {
        var connection = BuildConnection(accessToken: null);

        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());
    }

    private async Task<(ArenaPlayer Alice, ArenaPlayer Bob, Guid BattleId)> StartBattleAsync()
    {
        var alice = await ConnectAsync();
        var bob = await ConnectAsync();
        await alice.FindOpponentAsync();
        await bob.FindOpponentAsync();
        await alice.MatchFound.ReadAsync();
        var battleId = (await bob.MatchFound.ReadAsync()).Battle.Id;
        return (alice, bob, battleId);
    }

    private async Task<ArenaPlayer> ConnectAsync()
    {
        var http = _factory.CreateClient();
        var username = $"duel{Guid.NewGuid():N}"[..20];
        var response = await http.PostAsJsonAsync("/api/auth/register", new { Username = username, Password });
        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>(Json))!;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return await ConnectAsync(http, auth.AccessToken, username);
    }

    private async Task<ArenaPlayer> ConnectAsync(HttpClient http, string token, string username)
    {
        var player = new ArenaPlayer(BuildConnection(token), http, token, username);
        await player.Connection.StartAsync();
        return player;
    }

    private HubConnection BuildConnection(string? accessToken) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, ArenaHub.Path), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                if (accessToken is not null)
                {
                    options.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
                }
            })
            .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();

    private sealed class ArenaPlayer : IAsyncDisposable
    {
        private readonly Channel<PvpUpdate> _matchFound = Channel.CreateUnbounded<PvpUpdate>();
        private readonly Channel<PvpUpdate> _updates = Channel.CreateUnbounded<PvpUpdate>();

        public ArenaPlayer(HubConnection connection, HttpClient http, string token, string username)
        {
            Connection = connection;
            Http = http;
            Token = token;
            Username = username;
            connection.On<PvpUpdate>(nameof(IArenaClient.MatchFound), update => _matchFound.Writer.TryWrite(update));
            connection.On<PvpUpdate>(nameof(IArenaClient.BattleUpdated), update => _updates.Writer.TryWrite(update));
        }

        public HubConnection Connection { get; }
        public HttpClient Http { get; }
        public string Token { get; }
        public string Username { get; }

        public Reader MatchFound => new(_matchFound.Reader);
        public Reader Updates => new(_updates.Reader);

        public Task<bool> FindOpponentAsync() => Connection.InvokeAsync<bool>(nameof(ArenaHub.FindOpponent));

        public Task PlayCardAsync(Guid battleId, BattleCard card) =>
            Connection.InvokeAsync(nameof(ArenaHub.PlayCard), battleId, card);

        public Task ForfeitAsync(Guid battleId) => Connection.InvokeAsync(nameof(ArenaHub.Forfeit), battleId);

        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }

    private readonly struct Reader(ChannelReader<PvpUpdate> reader)
    {
        public async Task<PvpUpdate> ReadAsync()
        {
            using var timeout = new CancellationTokenSource(Wait);
            return await reader.ReadAsync(timeout.Token);
        }
    }
}
