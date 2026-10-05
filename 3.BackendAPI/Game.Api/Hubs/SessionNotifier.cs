using Microsoft.AspNetCore.SignalR;

namespace Game.Api.Hubs;

/// <summary>Tells a player's other browsers that a newer sign-in has replaced them.</summary>
public class SessionNotifier(
    IHubContext<SessionHub, ISessionClient> sessionHub,
    PvpMatchmaker matchmaker,
    ILogger<SessionNotifier> logger)
{
    /// <summary>
    /// Call this after the new session is saved and before its tokens are handed out, so only
    /// connections opened by earlier sessions receive the message.
    /// </summary>
    public async Task EndOtherSessionsAsync(Guid userId)
    {
        // An old browser waiting in the PvP lobby shouldn't be paired after it's been signed out.
        matchmaker.Leave(userId);

        try
        {
            await sessionHub.Clients.User(userId.ToString()).SessionEnded();
        }
        catch (Exception ex)
        {
            // The old tokens are already refused; this push only makes the old browser notice sooner.
            logger.LogWarning(ex, "Could not notify earlier sessions of user {UserId}.", userId);
        }
    }
}
