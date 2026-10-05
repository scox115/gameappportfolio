using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Game.Api.Hubs;

/// <summary>Messages about the player's sign-in session.</summary>
public interface ISessionClient
{
    /// <summary>The account signed in on another browser, so this one has been signed out.</summary>
    Task SessionEnded();
}

/// <summary>
/// Every signed-in browser stays connected here, so the server can tell it straight away when
/// the account signs in somewhere else. The client only listens; there is nothing to call.
/// </summary>
[Authorize]
public class SessionHub : Hub<ISessionClient>
{
    public const string Path = "/hubs/session";
}
