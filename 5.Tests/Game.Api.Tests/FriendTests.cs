using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Game.Api.Hubs;
using Game.Api.Models;
using Game.Core.Social;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Game.Api.Tests;

// Friend requests, the friends list, and challenging a friend to a duel (docs/adr/0037-friends-and-challenges.md).
public class FriendTests
{
    private const string Password = "Arena-Pass1";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task AHeroAsksAnother_WhoAccepts_AndTheyAreFriends()
    {
        using var factory = new GameApiFactory();
        var alice = await SignUpAsync(factory);
        var bob = await SignUpAsync(factory);
        await using var bobsSession = await ListenAsync(factory, bob);

        // Names are matched ignoring case, as at sign-in.
        var asked = await alice.Http.PostAsJsonAsync("/api/v1/friends", new { Username = bob.Username.ToUpperInvariant() });
        Assert.Equal(HttpStatusCode.Created, asked.StatusCode);
        await bobsSession.FriendsChanged.ReadAsync().AsTask().WaitAsync(Wait);

        var alicesList = await ListAsync(alice);
        Assert.Equal(bob.Id, Assert.Single(alicesList.Outgoing).Id);
        var bobsList = await ListAsync(bob);
        Assert.Equal(alice.Id, Assert.Single(bobsList.Incoming).Id);
        Assert.Empty(bobsList.Friends);

        (await bob.Http.PostAsync($"/api/v1/friends/{alice.Id}/accept", null)).EnsureSuccessStatusCode();

        var friend = Assert.Single((await ListAsync(alice)).Friends);
        Assert.Equal(bob.Username, friend.Username);
        Assert.True(friend.Online); // bob has the game open
        Assert.Null(friend.DuelId);
        Assert.Equal(alice.Id, Assert.Single((await ListAsync(bob)).Friends).Id);
    }

    [Fact]
    public async Task AskingSomeoneWhoAlreadyAskedYou_MakesYouFriends()
    {
        using var factory = new GameApiFactory();
        var alice = await SignUpAsync(factory);
        var bob = await SignUpAsync(factory);

        (await alice.Http.PostAsJsonAsync("/api/v1/friends", new { bob.Username })).EnsureSuccessStatusCode();
        var back = await bob.Http.PostAsJsonAsync("/api/v1/friends", new { alice.Username });

        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        Assert.Single((await ListAsync(alice)).Friends);
    }

    [Fact]
    public async Task RequestsThatCantBeMade_SayWhy()
    {
        using var factory = new GameApiFactory();
        var alice = await SignUpAsync(factory);
        var bob = await SignUpAsync(factory);
        var guest = await GuestAsync(factory);

        Assert.Equal(HttpStatusCode.NotFound, (await alice.Http.PostAsJsonAsync("/api/v1/friends", new { Username = "nobody-here" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await alice.Http.PostAsJsonAsync("/api/v1/friends", new { alice.Username })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await alice.Http.PostAsJsonAsync("/api/v1/friends", new { guest.Username })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await guest.Http.PostAsJsonAsync("/api/v1/friends", new { alice.Username })).StatusCode);

        (await alice.Http.PostAsJsonAsync("/api/v1/friends", new { bob.Username })).EnsureSuccessStatusCode();
        var again = await alice.Http.PostAsJsonAsync("/api/v1/friends", new { bob.Username });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("already asked", await again.Content.ReadAsStringAsync());

        // Only the hero who was asked can accept.
        Assert.Equal(HttpStatusCode.NotFound, (await alice.Http.PostAsync($"/api/v1/friends/{bob.Id}/accept", null)).StatusCode);
    }

    [Fact]
    public async Task RemovingAFriend_TakesThemOffBothLists()
    {
        using var factory = new GameApiFactory();
        var (alice, bob) = await FriendsAsync(factory);

        Assert.Equal(HttpStatusCode.NoContent, (await bob.Http.DeleteAsync($"/api/v1/friends/{alice.Id}")).StatusCode);

        Assert.Empty((await ListAsync(alice)).Friends);
        Assert.Empty((await ListAsync(bob)).Friends);
    }

    [Fact]
    public async Task DeletingAHero_RemovesThemFromTheirFriendsLists()
    {
        using var factory = new GameApiFactory();
        var (alice, bob) = await FriendsAsync(factory);

        (await bob.Http.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/v1/players/me")
        {
            Content = JsonContent.Create(new { Password })
        })).EnsureSuccessStatusCode();

        Assert.Empty((await ListAsync(alice)).Friends);
    }

    [Fact]
    public async Task AChallengedFriendWhoAccepts_StartsADuelForBoth()
    {
        using var factory = new GameApiFactory();
        var (alice, bob) = await FriendsAsync(factory);
        await using var bobsSession = await ListenAsync(factory, bob);
        await using var alicesArena = await ArenaAsync(factory, alice);
        await using var bobsArena = await ArenaAsync(factory, bob);

        var sent = await alicesArena.Hub.InvokeAsync<ChallengeSent?>(nameof(ArenaHub.ChallengeFriend), bob.Id);
        Assert.NotNull(sent);
        Assert.Equal(bob.Username, sent.FriendName);
        Assert.Equal((int)DuelChallenge.OpenFor.TotalSeconds, sent.SecondsLeft);

        var invite = await bobsSession.Challenges.ReadAsync().AsTask().WaitAsync(Wait);
        Assert.Equal(sent.Id, invite.Id);
        Assert.Equal(alice.Username, invite.FromName);

        await bobsArena.Hub.InvokeAsync(nameof(ArenaHub.AcceptChallenge), invite.Id);

        var alicesDuel = await alicesArena.MatchFound.ReadAsync().AsTask().WaitAsync(Wait);
        var bobsDuel = await bobsArena.MatchFound.ReadAsync().AsTask().WaitAsync(Wait);
        Assert.Equal(alicesDuel.Battle.Id, bobsDuel.Battle.Id);
        Assert.Equal(bob.Id, alicesDuel.Battle.Opponent.Id);
        Assert.Equal(0, alicesDuel.Battle.Wager);
        Assert.NotEqual(alicesDuel.Battle.YourTurn, bobsDuel.Battle.YourTurn);
        Assert.Equal(sent.Id, await bobsSession.Closed.ReadAsync().AsTask().WaitAsync(Wait)); // gone from bob's other tabs

        // Friends see the duel on their list, ready to watch.
        Assert.Equal(alicesDuel.Battle.Id, Assert.Single((await ListAsync(bob)).Friends).DuelId);

        // The challenge was used up.
        var twice = await Assert.ThrowsAnyAsync<Exception>(() => bobsArena.Hub.InvokeAsync(nameof(ArenaHub.AcceptChallenge), invite.Id));
        Assert.Contains("no longer open", twice.Message);
    }

    [Fact]
    public async Task AChallengeThatIsTurnedDown_TellsTheChallenger()
    {
        using var factory = new GameApiFactory();
        var (alice, bob) = await FriendsAsync(factory);
        await using var alicesArena = await ArenaAsync(factory, alice);
        await using var bobsArena = await ArenaAsync(factory, bob);
        var sent = await alicesArena.Hub.InvokeAsync<ChallengeSent?>(nameof(ArenaHub.ChallengeFriend), bob.Id);

        // From any screen, without the arena connection.
        Assert.Equal(HttpStatusCode.NoContent, (await bob.Http.PostAsync($"/api/v1/friends/challenges/{sent!.Id}/decline", null)).StatusCode);

        var reason = await alicesArena.Declined.ReadAsync().AsTask().WaitAsync(Wait);
        Assert.Contains(bob.Username, reason);
    }

    [Fact]
    public async Task AWithdrawnChallenge_DisappearsFromTheFriendsScreen()
    {
        using var factory = new GameApiFactory();
        var (alice, bob) = await FriendsAsync(factory);
        await using var bobsSession = await ListenAsync(factory, bob);
        var alicesArena = await ArenaAsync(factory, alice);
        var sent = await alicesArena.Hub.InvokeAsync<ChallengeSent?>(nameof(ArenaHub.ChallengeFriend), bob.Id);

        // Leaving the arena page takes the challenge back.
        await alicesArena.DisposeAsync();

        Assert.Equal(sent!.Id, await bobsSession.Closed.ReadAsync().AsTask().WaitAsync(Wait));
    }

    [Fact]
    public async Task AnOldChallenge_CantBeAccepted()
    {
        using var factory = new GameApiFactory();
        var (alice, bob) = await FriendsAsync(factory);
        await using var alicesArena = await ArenaAsync(factory, alice);
        await using var bobsArena = await ArenaAsync(factory, bob);
        var sent = await alicesArena.Hub.InvokeAsync<ChallengeSent?>(nameof(ArenaHub.ChallengeFriend), bob.Id);

        factory.Clock.Advance(DuelChallenge.OpenFor + TimeSpan.FromSeconds(1));

        var late = await Assert.ThrowsAnyAsync<Exception>(() => bobsArena.Hub.InvokeAsync(nameof(ArenaHub.AcceptChallenge), sent!.Id));
        Assert.Contains("ran out of time", late.Message);
    }

    [Fact]
    public async Task OnlyFriends_CanBeChallenged()
    {
        using var factory = new GameApiFactory();
        var alice = await SignUpAsync(factory);
        var stranger = await SignUpAsync(factory);
        (await alice.Http.PostAsJsonAsync("/api/v1/friends", new { stranger.Username })).EnsureSuccessStatusCode(); // asked, not accepted
        await using var alicesArena = await ArenaAsync(factory, alice);

        var refused = await Assert.ThrowsAnyAsync<Exception>(() =>
            alicesArena.Hub.InvokeAsync<ChallengeSent?>(nameof(ArenaHub.ChallengeFriend), stranger.Id));
        Assert.Contains("only challenge your friends", refused.Message);
    }

    private static async Task<(Hero Alice, Hero Bob)> FriendsAsync(GameApiFactory factory)
    {
        var alice = await SignUpAsync(factory);
        var bob = await SignUpAsync(factory);
        (await alice.Http.PostAsJsonAsync("/api/v1/friends", new { bob.Username })).EnsureSuccessStatusCode();
        (await bob.Http.PostAsync($"/api/v1/friends/{alice.Id}/accept", null)).EnsureSuccessStatusCode();
        return (alice, bob);
    }

    private static async Task<FriendsResponse> ListAsync(Hero hero) =>
        (await hero.Http.GetFromJsonAsync<FriendsResponse>("/api/v1/friends", Json))!;

    private static async Task<Hero> SignUpAsync(GameApiFactory factory)
    {
        var http = factory.CreateClient();
        var username = $"pal{Guid.NewGuid():N}"[..14];
        var response = await http.PostAsJsonAsync("/api/v1/auth/register", new { Username = username, Password });
        response.EnsureSuccessStatusCode();
        return Signed(http, (await response.Content.ReadFromJsonAsync<AuthResponse>(Json))!);
    }

    private static async Task<Hero> GuestAsync(GameApiFactory factory)
    {
        var http = factory.CreateClient();
        var response = await http.PostAsJsonAsync("/api/v1/auth/guest", new { });
        response.EnsureSuccessStatusCode();
        return Signed(http, (await response.Content.ReadFromJsonAsync<AuthResponse>(Json))!);
    }

    private static Hero Signed(HttpClient http, AuthResponse auth)
    {
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return new Hero(auth.Player.Id, auth.Player.Username, http, auth.AccessToken);
    }

    private static async Task<SessionListener> ListenAsync(GameApiFactory factory, Hero hero)
    {
        var hub = Build(factory, SessionHub.Path, hero.Token);
        var listener = new SessionListener(hub);
        await hub.StartAsync();
        return listener;
    }

    private static async Task<ArenaListener> ArenaAsync(GameApiFactory factory, Hero hero)
    {
        var hub = Build(factory, ArenaHub.Path, hero.Token);
        var listener = new ArenaListener(hub);
        await hub.StartAsync();
        return listener;
    }

    private static HubConnection Build(GameApiFactory factory, string path, string token) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, path), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();

    private sealed record Hero(Guid Id, string Username, HttpClient Http, string Token);

    private sealed class SessionListener : IAsyncDisposable
    {
        private readonly HubConnection _hub;
        private readonly Channel<bool> _friendsChanged = Channel.CreateUnbounded<bool>();
        private readonly Channel<ChallengeView> _challenges = Channel.CreateUnbounded<ChallengeView>();
        private readonly Channel<Guid> _closed = Channel.CreateUnbounded<Guid>();

        public SessionListener(HubConnection hub)
        {
            _hub = hub;
            hub.On(nameof(ISessionClient.FriendsChanged), () => _friendsChanged.Writer.TryWrite(true));
            hub.On<ChallengeView>(nameof(ISessionClient.ChallengeReceived), c => _challenges.Writer.TryWrite(c));
            hub.On<Guid>(nameof(ISessionClient.ChallengeClosed), id => _closed.Writer.TryWrite(id));
        }

        public ChannelReader<bool> FriendsChanged => _friendsChanged.Reader;
        public ChannelReader<ChallengeView> Challenges => _challenges.Reader;
        public ChannelReader<Guid> Closed => _closed.Reader;

        public ValueTask DisposeAsync() => _hub.DisposeAsync();
    }

    private sealed class ArenaListener : IAsyncDisposable
    {
        private readonly Channel<PvpUpdate> _matchFound = Channel.CreateUnbounded<PvpUpdate>();
        private readonly Channel<string> _declined = Channel.CreateUnbounded<string>();

        public ArenaListener(HubConnection hub)
        {
            Hub = hub;
            hub.On<PvpUpdate>(nameof(IArenaClient.MatchFound), u => _matchFound.Writer.TryWrite(u));
            hub.On<string>(nameof(IArenaClient.ChallengeDeclined), r => _declined.Writer.TryWrite(r));
        }

        public HubConnection Hub { get; }
        public ChannelReader<PvpUpdate> MatchFound => _matchFound.Reader;
        public ChannelReader<string> Declined => _declined.Reader;

        public ValueTask DisposeAsync() => Hub.DisposeAsync();
    }
}
