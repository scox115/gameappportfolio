using Game.Api.Hubs;
using Game.Core.Battles;
using Game.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Game.Api.Tests;

// The lobby is a table shared by every API replica. Every test runs on SQLite and, when TEST_SQLSERVER is set,
// on SQL Server: both save each SaveChanges in a transaction and report a delete of a row that's already gone,
// which the pairing relies on and the in-memory provider doesn't do.
public sealed class MatchmakerTests : IDisposable
{
    private readonly List<ServiceProvider> _replicas = [];
    private readonly TestClock _clock = new();
    private TestDatabase? _database;

    [Theory, MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public async Task PlayersOnTheSameNetwork_ArePairedForAFriendlyDuel_ButFlagged(string engine)
    {
        var matchmaker = Replica(engine);
        var alice = Guid.NewGuid();

        Assert.Null(await matchmaker.JoinOrPairAsync(alice, 0, "203.0.113.7"));
        var pairing = await matchmaker.JoinOrPairAsync(Guid.NewGuid(), 0, "203.0.113.7");

        Assert.Equal(new PvpPairing(alice, SameNetwork: true), pairing);
    }

    [Theory, MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public async Task WagerDuels_SkipOpponentsOnTheSameNetwork(string engine)
    {
        var matchmaker = Replica(engine);
        var alice = Guid.NewGuid();
        var carol = Guid.NewGuid();
        await matchmaker.JoinOrPairAsync(alice, 100, "203.0.113.7", avoidSameNetwork: true);

        Assert.Null(await matchmaker.JoinOrPairAsync(Guid.NewGuid(), 100, "203.0.113.7", avoidSameNetwork: true));
        var pairing = await matchmaker.JoinOrPairAsync(carol, 100, "198.51.100.9", avoidSameNetwork: true);

        Assert.Equal(new PvpPairing(alice, SameNetwork: false), pairing);
    }

    [Theory, MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public async Task UnknownAddresses_NeverCountAsTheSameNetwork(string engine)
    {
        var matchmaker = Replica(engine);
        await matchmaker.JoinOrPairAsync(Guid.NewGuid(), 0, network: null);

        Assert.False((await matchmaker.JoinOrPairAsync(Guid.NewGuid(), 0, network: null))!.SameNetwork);
    }

    [Theory, MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public async Task OnlyPlayersWithTheSameWager_ArePaired(string engine)
    {
        var matchmaker = Replica(engine);
        await matchmaker.JoinOrPairAsync(Guid.NewGuid(), 50);

        Assert.Null(await matchmaker.JoinOrPairAsync(Guid.NewGuid(), 100));
    }

    [Theory, MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public async Task APlayerWaitingOnOneReplica_IsPairedFromAnother(string engine)
    {
        var east = Replica(engine);
        var west = Replica(engine);
        var alice = Guid.NewGuid();

        Assert.Null(await east.JoinOrPairAsync(alice));
        var pairing = await west.JoinOrPairAsync(Guid.NewGuid());

        Assert.Equal(alice, pairing!.OpponentId);
        Assert.False(await east.IsWaitingAsync(alice));
    }

    [Theory, MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public async Task LeavingOnAnyReplica_TakesThePlayerOutOfTheLobby(string engine)
    {
        var east = Replica(engine);
        var west = Replica(engine);
        var alice = Guid.NewGuid();
        await east.JoinOrPairAsync(alice);

        await west.LeaveAsync(alice);

        Assert.Null(await west.JoinOrPairAsync(Guid.NewGuid()));
    }

    [Theory, MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public async Task AnEntryNobodyVouchesFor_StopsPairing(string engine)
    {
        // The replica holding Alice's connection crashed, so nothing refreshes her entry.
        var crashed = Replica(engine);
        var live = Replica(engine);
        await crashed.JoinOrPairAsync(Guid.NewGuid());
        _clock.Advance(PvpLobbyEntry.StaleAfter + TimeSpan.FromSeconds(1));

        Assert.Null(await live.JoinOrPairAsync(Guid.NewGuid()));
    }

    [Theory, MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public async Task TheHeartbeat_KeepsWaitingPlayersInTheLobby(string engine)
    {
        var east = Replica(engine);
        var west = Replica(engine);
        var alice = Guid.NewGuid();
        await east.JoinOrPairAsync(alice);

        for (var i = 0; i < 4; i++)
        {
            _clock.Advance(PvpLobbyEntry.HeartbeatInterval);
            await east.HeartbeatAsync();
        }

        Assert.Equal(alice, (await west.JoinOrPairAsync(Guid.NewGuid()))!.OpponentId);
    }

    [Theory, MemberData(nameof(TestDatabase.Engines), MemberType = typeof(TestDatabase))]
    public async Task ManyPlayersJoiningAtOnceOnTwoReplicas_AreEachPairedExactlyOnce(string engine)
    {
        var replicas = new[] { Replica(engine), Replica(engine) };
        var players = Enumerable.Range(0, 40).Select(_ => Guid.NewGuid()).ToList();

        var results = await Task.WhenAll(players.Select((player, i) =>
            Task.Run(() => replicas[i % 2].JoinOrPairAsync(player))));

        // Each pairing is reported once, by the player who took the opponent.
        var pairs = players.Zip(results).Where(r => r.Second is not null).Select(r => (r.First, r.Second!.OpponentId)).ToList();
        var everyonePaired = pairs.SelectMany(p => new[] { p.First, p.OpponentId }).ToList();
        Assert.Equal(everyonePaired.Count, everyonePaired.Distinct().Count());

        // Nobody is stranded: with an even number of players, all of them are paired.
        using var db = _database!.NewContext();
        Assert.Empty(await db.PvpLobby.ToListAsync());
        Assert.Equal(20, pairs.Count);
    }

    // An API replica's matchmaker. All of a test's replicas share one database.
    private PvpMatchmaker Replica(string engine)
    {
        _database ??= TestDatabase.Create(engine);
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<TimeProvider>(_clock)
            .AddDbContext<AppDbContext>(_database.Configure)
            .AddSingleton<PvpMatchmaker>()
            .BuildServiceProvider();
        _replicas.Add(services);
        return services.GetRequiredService<PvpMatchmaker>();
    }

    public void Dispose()
    {
        foreach (var replica in _replicas) replica.Dispose();
        _database?.Dispose();
    }
}
