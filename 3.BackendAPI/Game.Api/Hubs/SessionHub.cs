using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Game.Api.Hubs;

/// <summary>Messages about the player's sign-in session.</summary>
public interface ISessionClient
{
    /// <summary>The account signed in on another browser, so this one has been signed out.</summary>
    Task SessionEnded();

    /// <summary>An admin suspended the account, so every browser has been signed out.</summary>
    Task Suspended();

    /// <summary>Someone sent, accepted or withdrew a friend request, or removed this hero as a friend: reload the list.</summary>
    Task FriendsChanged();

    /// <summary>A friend challenged this hero to a duel.</summary>
    Task ChallengeReceived(Models.ChallengeView challenge);

    /// <summary>The challenge was withdrawn, answered in another tab, or ran out of time: stop showing it.</summary>
    Task ChallengeClosed(Guid challengeId);
}

/// <summary>
/// Every signed-in browser stays connected here, so the server can tell it straight away when
/// the account signs in somewhere else. The client only listens; there is nothing to call.
/// The same connections tell the live lobby who is online (see <see cref="ArenaPulse"/>), and bring
/// friend requests and duel challenges wherever the hero is in the game.
/// </summary>
[Authorize]
public class SessionHub(ArenaPulse pulse) : Hub<ISessionClient>
{
    public const string Path = "/hubs/session";

    public override async Task OnConnectedAsync()
    {
        await pulse.PlayerConnectedAsync(Guid.Parse(Context.UserIdentifier!), Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await pulse.PlayerDisconnectedAsync(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
