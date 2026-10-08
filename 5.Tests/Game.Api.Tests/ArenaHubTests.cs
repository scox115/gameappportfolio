using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Game.Api.Hubs;
using Game.Api.Models;
using Game.Core.Battles;
using Game.Core.Entities;
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
            // Both are new Sorcerers, whose class boosts Fireball.
            var fireball = HeroClasses.CardFor(HeroClass.Sorcerer, BattleCard.Fireball, 1).Damage;
            Assert.Equal(fireball, aliceUpdate.LastTurn.DamageDealt);
            Assert.Equal(100 - fireball, aliceUpdate.Battle.You.Hp);
            Assert.Equal(100 - fireball, bobUpdate.Battle.Opponent.Hp);
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

            var bobProfile = await bob.Http.GetFromJsonAsync<PlayerProfileResponse>("/api/v1/players/me", Json);
            Assert.Equal(Player.StartingGold + MatchRulesEngine.WinGold, bobProfile!.Gold);

            var current = await bob.Http.GetAsync("/api/v1/battles/pvp/current");
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
    public async Task AWageredDuel_TakesBothStakesAndPaysTheWinnerThePot()
    {
        await using var alice = await ConnectAsync();
        await using var bob = await ConnectAsync();

        Assert.True(await alice.FindWageredOpponentAsync(100));
        Assert.False(await bob.FindWageredOpponentAsync(100));
        var battle = (await alice.MatchFound.ReadAsync()).Battle;
        await bob.MatchFound.ReadAsync();
        Assert.Equal(100, battle.Wager);
        var staked = await alice.Http.GetFromJsonAsync<PlayerProfileResponse>("/api/v1/players/me", Json);
        Assert.Equal(Player.StartingGold - 100, staked!.Gold);

        // Bob joined second, so he moves first.
        await PlayMovesAsync(bob, alice, battle.Id, 2 * DuelRewardRules.MinMovesEach);
        await alice.ForfeitAsync(battle.Id);

        var bobReward = (await bob.Updates.ReadAsync()).Reward!;
        var aliceReward = (await alice.Updates.ReadAsync()).Reward!;
        Assert.Equal(DuelWagers.Payout(100), bobReward.WagerResult);
        Assert.Equal(-100, aliceReward.WagerResult);
        Assert.Equal(Player.StartingGold - 100 + MatchRulesEngine.WinGold + DuelWagers.Payout(100) + BonusGold(bobReward), bobReward.Player.Gold);
        Assert.Equal(Player.StartingGold - 100 + MatchRulesEngine.LossGold + BonusGold(aliceReward), aliceReward.Player.Gold);
    }

    [Fact]
    public async Task PlayersWithDifferentWagers_AreNotPaired()
    {
        await using var alice = await ConnectAsync();
        await using var bob = await ConnectAsync();

        Assert.True(await alice.FindWageredOpponentAsync(100));
        Assert.True(await bob.FindWageredOpponentAsync(50));

        await alice.Connection.InvokeAsync(nameof(ArenaHub.CancelSearch));
        await bob.Connection.InvokeAsync(nameof(ArenaHub.CancelSearch));
    }

    [Fact]
    public async Task AWagerNeedsTheGold()
    {
        await using var alice = await ConnectAsync();

        var tooRich = await Assert.ThrowsAnyAsync<Exception>(() => alice.FindWageredOpponentAsync(DuelWagers.Stakes.Max() * 2));
        Assert.Contains("Wagers can be", tooRich.Message);

        await using var bob = await ConnectAsync(); // a new hero's starting gold is less than 500
        var broke = await Assert.ThrowsAnyAsync<Exception>(() => bob.FindWageredOpponentAsync(500));
        Assert.Contains("You need 500 gold", broke.Message);
    }

    [Fact]
    public async Task AGuest_CanDuelForFun_ButNotForGold()
    {
        var http = _factory.CreateClient();
        var response = await http.PostAsJsonAsync("/api/v1/auth/guest", new { });
        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>(Json))!;
        await using var guest = await ConnectAsync(http, auth.AccessToken, auth.Player.Username);

        var wager = await Assert.ThrowsAnyAsync<Exception>(() => guest.FindWageredOpponentAsync(50));
        Assert.Contains("Save your hero to duel for gold", wager.Message);
        Assert.True(await guest.FindOpponentAsync()); // waiting for a friendly duel is fine
        await guest.Connection.InvokeAsync(nameof(ArenaHub.CancelSearch));
    }

    [Fact]
    public async Task ThePlayerCanDuelTheArenaBot_WhichPlaysItsOwnTurns_ForNoRewards()
    {
        await using var alice = await ConnectAsync();
        Assert.True(await alice.FindOpponentAsync()); // nobody else around
        (await alice.Http.PostAsJsonAsync("/api/v1/shop/purchases", new { Item = "DuelElixir" }, Json)).EnsureSuccessStatusCode();
        var before = (await alice.Http.GetFromJsonAsync<PlayerProfileResponse>("/api/v1/players/me", Json))!;

        await alice.Connection.InvokeAsync(nameof(ArenaHub.DuelBot));

        var battle = (await alice.MatchFound.ReadAsync()).Battle;
        Assert.True(battle.AgainstBot);
        Assert.True(battle.Practice);
        Assert.Equal(ArenaBot.Name, battle.Opponent.Username);
        Assert.Equal(PvpBattle.BasePlayerMaxHp, battle.You.MaxHp); // the elixir is saved for a duel that counts

        // The bot answers every move on its own; the duel always ends, one way or the other.
        BattleRewardResponse? reward = null;
        var botMoves = 0;
        while (battle.Status != PvpBattleStatus.Finished)
        {
            if (battle.YourTurn) await alice.PlayCardAsync(battle.Id, BattleCard.Fireball);
            var update = await alice.Updates.ReadAsync();
            if (update.LastTurn is { YourCard: false }) botMoves++;
            battle = update.Battle;
            reward = update.Reward ?? reward;
        }

        Assert.True(botMoves > 0);
        Assert.Contains("Arena Bot", reward!.NoRewardReason);
        Assert.Equal((before.Gold, before.Rating, before.PvpWins + before.PvpLosses),
            (reward.Player.Gold, reward.Player.Rating, reward.Player.PvpWins + reward.Player.PvpLosses));
    }

    [Fact]
    public async Task TheArenaBot_IsLeftOffTheLeaderboardAndTheHeroCount()
    {
        await using var alice = await ConnectAsync();
        await alice.Connection.InvokeAsync(nameof(ArenaHub.DuelBot));
        var battle = (await alice.MatchFound.ReadAsync()).Battle;
        await alice.ForfeitAsync(battle.Id);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Game.Infrastructure.Data.AppDbContext>();
        Assert.NotNull(await db.Players.FindAsync(ArenaBot.Id));
        var leaders = await alice.Http.GetStringAsync("/api/v1/players/leaderboard");
        Assert.DoesNotContain("Arena Bot", leaders);
        var stats = JsonDocument.Parse(await alice.Http.GetStringAsync("/api/v1/players/stats")).RootElement;
        Assert.Equal(db.Players.Count() - 1, stats.GetProperty("registeredPlayers").GetInt32());
    }

    [Fact]
    public async Task ABotDuel_IsMeasuredAsAPracticeDuelAgainstTheBot()
    {
        var duels = new System.Collections.Concurrent.ConcurrentBag<Dictionary<string, object?>>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == Game.Api.Observability.GameTelemetry.Name && instrument.Name == "game.battles.completed")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var values = new Dictionary<string, object?>();
            foreach (var tag in tags) values[tag.Key] = tag.Value;
            duels.Add(values);
        });
        listener.Start();

        await using var alice = await ConnectAsync();
        await alice.Connection.InvokeAsync(nameof(ArenaHub.DuelBot));
        var battle = (await alice.MatchFound.ReadAsync()).Battle;
        await alice.ForfeitAsync(battle.Id);

        // Other tests duel at the same time, so look for this one among them.
        Assert.Contains(duels, d => (string?)d["game.battle.kind"] == "pvp" && (string?)d["game.battle.outcome"] == "Forfeit"
            && (string?)d["game.duel.opponent"] == "bot" && d["game.duel.counted"] is false);
    }

    [Fact]
    public async Task Winning_MovesRatingPointsFromTheLoserToTheWinner()
    {
        var (alice, bob, battleId) = await StartBattleAsync();
        await using (alice)
        await using (bob)
        {
            await PlayMovesAsync(bob, alice, battleId, 2 * DuelRewardRules.MinMovesEach);
            await alice.ForfeitAsync(battleId);

            var bobUpdate = await bob.Updates.ReadAsync();
            var aliceUpdate = await alice.Updates.ReadAsync();
            var points = EloRating.PointsForWin(EloRating.StartingRating, EloRating.StartingRating);
            Assert.Equal(points, bobUpdate.Reward!.RatingChange);
            Assert.Equal(-points, aliceUpdate.Reward!.RatingChange);
            Assert.Equal(EloRating.StartingRating + points, bobUpdate.Battle.You.Rating);
            Assert.Equal(EloRating.StartingRating - points, bobUpdate.Battle.Opponent.Rating);

            var aliceProfile = await alice.Http.GetFromJsonAsync<PlayerProfileResponse>("/api/v1/players/me", Json);
            Assert.Equal(EloRating.StartingRating - points, aliceProfile!.Rating);
            Assert.Equal(1, aliceProfile.PvpLosses);
            Assert.Equal(0, aliceProfile.PvpWins);
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

            var current = await alice.Http.GetFromJsonAsync<PvpBattleView>("/api/v1/battles/pvp/current", Json);
            Assert.Equal(battleId, current!.Id);

            await using var aliceAgain = await ConnectAsync(alice.Http, alice.Token, alice.Username);
            Assert.False(await aliceAgain.FindOpponentAsync());
            Assert.Equal(battleId, (await aliceAgain.MatchFound.ReadAsync()).Battle.Id);

            await bob.ForfeitAsync(battleId);
        }
    }

    [Fact]
    public async Task UpgradesAndDuelElixirsCarryIntoTheDuel()
    {
        // Bob joins second, so he goes first (see the class comment).
        var alice = await ConnectAsync();
        var bob = await ConnectAsync();
        await using (alice)
        await using (bob)
        {
            var bobProfile = await bob.Http.GetFromJsonAsync<PlayerProfileResponse>("/api/v1/players/me", Json);
            await _factory.GiveGoldAsync(bobProfile!.Id, 500);
            (await bob.Http.PostAsJsonAsync("/api/v1/shop/purchases", new { Item = "FireballUpgrade" })).EnsureSuccessStatusCode();
            (await bob.Http.PostAsJsonAsync("/api/v1/shop/purchases", new { Item = "DuelElixir" })).EnsureSuccessStatusCode();

            await alice.FindOpponentAsync();
            await bob.FindOpponentAsync();
            await alice.MatchFound.ReadAsync();
            var bobView = (await bob.MatchFound.ReadAsync()).Battle;

            Assert.Equal(PvpBattle.BasePlayerMaxHp + Player.DuelElixirBonusHp, bobView.You.MaxHp);
            Assert.Equal(PvpBattle.BasePlayerMaxHp, bobView.Opponent.MaxHp);
            var upgraded = HeroClasses.CardFor(HeroClass.Sorcerer, BattleCard.Fireball, 2).Damage;
            Assert.Equal(upgraded, bobView.YourCards.Single(c => c.Card == BattleCard.Fireball).Damage);

            await bob.PlayCardAsync(bobView.Id, BattleCard.Fireball);
            var aliceUpdate = await alice.Updates.ReadAsync();
            Assert.Equal(upgraded, aliceUpdate.LastTurn!.DamageDealt);

            await bob.Updates.ReadAsync();
            await alice.ForfeitAsync(bobView.Id);
        }
    }

    [Fact]
    public async Task TheHubRequiresSignIn()
    {
        var connection = BuildConnection(accessToken: null);

        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());
    }

    [Fact]
    public async Task AnInstantForfeit_DoesntCount_AndRefundsTheWager()
    {
        await using var alice = await ConnectAsync();
        await using var bob = await ConnectAsync();
        await alice.FindWageredOpponentAsync(50);
        await bob.FindWageredOpponentAsync(50);
        var battle = (await alice.MatchFound.ReadAsync()).Battle;
        await bob.MatchFound.ReadAsync();

        await alice.ForfeitAsync(battle.Id);

        var bobReward = (await bob.Updates.ReadAsync()).Reward!;
        var aliceReward = (await alice.Updates.ReadAsync()).Reward!;
        Assert.Contains("doesn't count", bobReward.NoRewardReason);
        Assert.Equal(0, bobReward.GoldEarned);
        Assert.Equal(Player.StartingGold, bobReward.Player.Gold);
        Assert.Equal(Player.StartingGold, aliceReward.Player.Gold);
        Assert.Equal(EloRating.StartingRating, bobReward.Player.Rating);
        Assert.Equal(0, bobReward.Player.PvpWins);
    }

    [Fact]
    public async Task OnlyTheFirstFewDuelsADayAgainstTheSameOpponentCount()
    {
        await using var alice = await ConnectAsync();
        await using var bob = await ConnectAsync();

        var rewards = new List<BattleRewardResponse>();
        for (var i = 0; i <= DuelRewardRules.RewardedDuelsPerOpponentPerDay; i++)
        {
            await alice.FindOpponentAsync();
            await bob.FindOpponentAsync();
            var battleId = (await alice.MatchFound.ReadAsync()).Battle.Id;
            await bob.MatchFound.ReadAsync();
            await PlayMovesAsync(bob, alice, battleId, 2 * DuelRewardRules.MinMovesEach);
            await alice.ForfeitAsync(battleId);
            rewards.Add((await bob.Updates.ReadAsync()).Reward!);
            await alice.Updates.ReadAsync();
        }

        Assert.All(rewards.Take(DuelRewardRules.RewardedDuelsPerOpponentPerDay), r => Assert.Null(r.NoRewardReason));
        Assert.Contains("against this opponent today", rewards.Last().NoRewardReason);
        Assert.Equal(DuelRewardRules.RewardedDuelsPerOpponentPerDay, rewards.Last().Player.PvpWins);
    }

    // Plays Fireball back and forth, starting with whoever's turn it is, and drains both players' updates.
    private static async Task PlayMovesAsync(ArenaPlayer first, ArenaPlayer second, Guid battleId, int moves)
    {
        for (var move = 0; move < moves; move++)
        {
            await (move % 2 == 0 ? first : second).PlayCardAsync(battleId, BattleCard.Fireball);
            await first.Updates.ReadAsync();
            await second.Updates.ReadAsync();
        }
    }

    // Streak bonuses and any bounty today's duel completed.
    private static int BonusGold(BattleRewardResponse reward) => reward.StreakBonus + reward.BountiesCompleted!.Sum(b => b.Reward);

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
        var response = await http.PostAsJsonAsync("/api/v1/auth/register", new { Username = username, Password });
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

        public Task<bool> FindWageredOpponentAsync(int wager) =>
            Connection.InvokeAsync<bool>(nameof(ArenaHub.FindWageredOpponent), wager);

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
