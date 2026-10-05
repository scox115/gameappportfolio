using Game.Api.Auth;
using Microsoft.AspNetCore.SignalR;

namespace Game.Api.Hubs;

// Lets the server address a player's connections with Clients.User(playerId).
public class SubjectUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) =>
        connection.User?.Identity?.IsAuthenticated == true ? connection.User.GetPlayerId().ToString() : null;
}
