using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Game.Api.Hubs;
using Game.Api.Models;
using Game.Core.Battles;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Game.Api.Tests;

// Two API replicas share one database, the way Azure runs the API with more than one replica. Each player
// connects to a different replica, as the load balancer may decide, and still plays against the other.
public sealed class ScaleOutTests : IAsyncLifetime
{
    private const string Password = "Arena-Pass1";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private GameApiFactory _east = null!;
    private GameApiFactory _west = null!;
    private TestDatabase? _sqlServer;

    /// <summary>The in-memory database always; SQL Server too when TEST_SQLSERVER is set.</summary>
    public static TheoryData<string> Databases()
    {
        var databases = new TheoryData<string> { "in-memory" };
        if (TestDatabase.SqlServerConnection is not null) databases.Add(TestDatabase.SqlServer);
        return databases;
    }

    [Theory, MemberData(nameof(Databases))]
    public async Task PlayersOnDifferentReplicas_ArePairedAndSeeEachOthersMoves(string database)
    {
        StartReplicas(database);
        await using var alice = await JoinAsync(_east);
        await using var bob = await JoinAsync(_west);

        Assert.True(await alice.Connection.InvokeAsync<bool>(nameof(ArenaHub.FindOpponent)));
        Assert.False(await bob.Connection.InvokeAsync<bool>(nameof(ArenaHub.FindOpponent)));

        // Bob's replica started the duel; Alice hears about it through the other replica.
        var aliceView = (await alice.ReadAsync(alice.MatchFound)).Battle;
        var bobView = (await bob.ReadAsync(bob.MatchFound)).Battle;
        Assert.Equal(aliceView.Id, bobView.Id);
        Assert.Equal(bob.Username, aliceView.Opponent.Username);

        // Bob joined second, so he goes first; his move reaches Alice on the other replica.
        await bob.Connection.InvokeAsync(nameof(ArenaHub.PlayCard), bobView.Id, BattleCard.Fireball);
        var aliceUpdate = await alice.ReadAsync(alice.Updates);
        Assert.False(aliceUpdate.LastTurn!.YourCard);
        Assert.True(aliceUpdate.Battle.YourTurn);
        Assert.True((await bob.ReadAsync(bob.Updates)).LastTurn!.YourCard);

        // And Alice's answer reaches Bob.
        await alice.Connection.InvokeAsync(nameof(ArenaHub.PlayCard), aliceView.Id, BattleCard.Fireball);
        Assert.True((await bob.ReadAsync(bob.Updates)).Battle.YourTurn);
        Assert.True((await alice.ReadAsync(alice.Updates)).LastTurn!.YourCard);
    }

    [Theory, MemberData(nameof(Databases))]
    public async Task SigningInThroughAnotherReplica_TellsTheOldBrowserAtOnce(string database)
    {
        StartReplicas(database);
        var username = NewUsername();
        var first = await SignInAsync(_east, "register", username);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = Connect(_east, SessionHub.Path, first);
        connection.On(nameof(ISessionClient.SessionEnded), () => ended.TrySetResult());
        await connection.StartAsync();

        await SignInAsync(_west, "login", username);

        await ended.Task.WaitAsync(Wait);
    }

    private void StartReplicas(string database)
    {
        if (database == TestDatabase.SqlServer) _sqlServer = TestDatabase.ForApi();
        (_east, _west) = GameApiFactory.TwoReplicas(_sqlServer);
        // One at a time, as a deploy brings replicas up: the first creates the database and runs the migrations.
        _ = _east.Server;
        _ = _west.Server;
    }

    private static async Task<Player> JoinAsync(GameApiFactory replica)
    {
        var username = NewUsername();
        var player = new Player(Connect(replica, ArenaHub.Path, await SignInAsync(replica, "register", username)), username);
        await player.Connection.StartAsync();
        return player;
    }

    private static async Task<string> SignInAsync(GameApiFactory replica, string action, string username)
    {
        var response = await replica.CreateClient().PostAsJsonAsync($"/api/v1/auth/{action}", new { Username = username, Password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!.AccessToken;
    }

    // WebSockets with no negotiate request, as the browser connects.
    private static HubConnection Connect(GameApiFactory replica, string path, string accessToken) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(replica.Server.BaseAddress, path), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var uri = new UriBuilder(context.Uri) { Query = $"access_token={Uri.EscapeDataString(accessToken)}" }.Uri;
                    return await replica.Server.CreateWebSocketClient().ConnectAsync(uri, cancellationToken);
                };
            })
            .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();

    private static string NewUsername() => $"wide{Guid.NewGuid():N}"[..20];

    private sealed class Player : IAsyncDisposable
    {
        public Player(HubConnection connection, string username)
        {
            Connection = connection;
            Username = username;
            connection.On<PvpUpdate>(nameof(IArenaClient.MatchFound), update => MatchFound.Writer.TryWrite(update));
            connection.On<PvpUpdate>(nameof(IArenaClient.BattleUpdated), update => Updates.Writer.TryWrite(update));
        }

        public HubConnection Connection { get; }
        public string Username { get; }
        public Channel<PvpUpdate> MatchFound { get; } = Channel.CreateUnbounded<PvpUpdate>();
        public Channel<PvpUpdate> Updates { get; } = Channel.CreateUnbounded<PvpUpdate>();

        public async Task<PvpUpdate> ReadAsync(Channel<PvpUpdate> channel)
        {
            using var timeout = new CancellationTokenSource(Wait);
            return await channel.Reader.ReadAsync(timeout.Token);
        }

        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_east is not null) await _east.DisposeAsync();
        if (_west is not null) await _west.DisposeAsync();
        _sqlServer?.Dispose();
    }
}
