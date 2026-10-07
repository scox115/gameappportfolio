using Game.Api.Hubs;
using Game.Core.Battles;
using Game.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Game.Api.Tests;

// The lobby is a table shared by every API replica. These tests run it on a SQLite file, which (like
// SQL Server) saves each SaveChanges in a transaction and reports a delete of a row that's already gone.
public sealed class MatchmakerTests : IDisposable
{
    private readonly string _databaseFile = Path.Combine(Path.GetTempPath(), $"lobby-{Guid.NewGuid()}.db");
    private readonly List<ServiceProvider> _replicas = [];
    private readonly TestClock _clock = new();

    public MatchmakerTests()
    {
        using var db = NewDbContext();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task PlayersOnTheSameNetwork_ArePairedForAFriendlyDuel_ButFlagged()
    {
        var matchmaker = Replica();
        var alice = Guid.NewGuid();

        Assert.Null(await matchmaker.JoinOrPairAsync(alice, 0, "203.0.113.7"));
        var pairing = await matchmaker.JoinOrPairAsync(Guid.NewGuid(), 0, "203.0.113.7");

        Assert.Equal(new PvpPairing(alice, SameNetwork: true), pairing);
    }

    [Fact]
    public async Task WagerDuels_SkipOpponentsOnTheSameNetwork()
    {
        var matchmaker = Replica();
        var alice = Guid.NewGuid();
        var carol = Guid.NewGuid();
        await matchmaker.JoinOrPairAsync(alice, 100, "203.0.113.7", avoidSameNetwork: true);

        Assert.Null(await matchmaker.JoinOrPairAsync(Guid.NewGuid(), 100, "203.0.113.7", avoidSameNetwork: true));
        var pairing = await matchmaker.JoinOrPairAsync(carol, 100, "198.51.100.9", avoidSameNetwork: true);

        Assert.Equal(new PvpPairing(alice, SameNetwork: false), pairing);
    }

    [Fact]
    public async Task UnknownAddresses_NeverCountAsTheSameNetwork()
    {
        var matchmaker = Replica();
        await matchmaker.JoinOrPairAsync(Guid.NewGuid(), 0, network: null);

        Assert.False((await matchmaker.JoinOrPairAsync(Guid.NewGuid(), 0, network: null))!.SameNetwork);
    }

    [Fact]
    public async Task OnlyPlayersWithTheSameWager_ArePaired()
    {
        var matchmaker = Replica();
        await matchmaker.JoinOrPairAsync(Guid.NewGuid(), 50);

        Assert.Null(await matchmaker.JoinOrPairAsync(Guid.NewGuid(), 100));
    }

    [Fact]
    public async Task APlayerWaitingOnOneReplica_IsPairedFromAnother()
    {
        var east = Replica();
        var west = Replica();
        var alice = Guid.NewGuid();

        Assert.Null(await east.JoinOrPairAsync(alice));
        var pairing = await west.JoinOrPairAsync(Guid.NewGuid());

        Assert.Equal(alice, pairing!.OpponentId);
        Assert.False(await east.IsWaitingAsync(alice));
    }

    [Fact]
    public async Task LeavingOnAnyReplica_TakesThePlayerOutOfTheLobby()
    {
        var east = Replica();
        var west = Replica();
        var alice = Guid.NewGuid();
        await east.JoinOrPairAsync(alice);

        await west.LeaveAsync(alice);

        Assert.Null(await west.JoinOrPairAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task AnEntryNobodyVouchesFor_StopsPairing()
    {
        // The replica holding Alice's connection crashed, so nothing refreshes her entry.
        var crashed = Replica();
        var live = Replica();
        await crashed.JoinOrPairAsync(Guid.NewGuid());
        _clock.Advance(PvpLobbyEntry.StaleAfter + TimeSpan.FromSeconds(1));

        Assert.Null(await live.JoinOrPairAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task TheHeartbeat_KeepsWaitingPlayersInTheLobby()
    {
        var east = Replica();
        var west = Replica();
        var alice = Guid.NewGuid();
        await east.JoinOrPairAsync(alice);

        for (var i = 0; i < 4; i++)
        {
            _clock.Advance(PvpLobbyEntry.HeartbeatInterval);
            await east.HeartbeatAsync();
        }

        Assert.Equal(alice, (await west.JoinOrPairAsync(Guid.NewGuid()))!.OpponentId);
    }

    [Fact]
    public async Task ManyPlayersJoiningAtOnceOnTwoReplicas_AreEachPairedExactlyOnce()
    {
        var replicas = new[] { Replica(), Replica() };
        var players = Enumerable.Range(0, 40).Select(_ => Guid.NewGuid()).ToList();

        var results = await Task.WhenAll(players.Select((player, i) =>
            Task.Run(() => replicas[i % 2].JoinOrPairAsync(player))));

        // Each pairing is reported once, by the player who took the opponent.
        var pairs = players.Zip(results).Where(r => r.Second is not null).Select(r => (r.First, r.Second!.OpponentId)).ToList();
        var everyonePaired = pairs.SelectMany(p => new[] { p.First, p.OpponentId }).ToList();
        Assert.Equal(everyonePaired.Count, everyonePaired.Distinct().Count());

        // Nobody is stranded: with an even number of players, all of them are paired.
        using var db = NewDbContext();
        Assert.Empty(await db.PvpLobby.ToListAsync());
        Assert.Equal(20, pairs.Count);
    }

    private PvpMatchmaker Replica()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<TimeProvider>(_clock)
            .AddDbContext<AppDbContext>(options => options.UseSqlite(ConnectionString))
            .AddSingleton<PvpMatchmaker>()
            .BuildServiceProvider();
        _replicas.Add(services);
        return services.GetRequiredService<PvpMatchmaker>();
    }

    // Writers wait their turn for up to 30 seconds rather than failing straight away.
    private string ConnectionString => $"Data Source={_databaseFile};Default Timeout=30;Pooling=False";

    private AppDbContext NewDbContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(ConnectionString).Options);

    public void Dispose()
    {
        foreach (var replica in _replicas) replica.Dispose();
        File.Delete(_databaseFile);
    }
}
