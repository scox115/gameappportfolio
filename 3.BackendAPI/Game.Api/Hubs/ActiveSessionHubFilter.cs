using Game.Api.Auth;
using Microsoft.AspNetCore.SignalR;

namespace Game.Api.Hubs;

/// <summary>
/// A hub connection is authenticated once, when it opens. This re-checks the session on every
/// call, so a browser that was signed out by a newer sign-in can't keep playing over an old connection.
/// Every call is refused with a message the browser can show, instead of the connection dropping silently.
/// </summary>
public class ActiveSessionHubFilter(ActiveSessionValidator sessions) : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var connection = invocationContext.Context;
        if (connection.User is not { } user || !await sessions.IsCurrentAsync(user, connection.ConnectionAborted))
        {
            throw new HubException("You signed in on another browser, so this one was signed out.");
        }

        return await next(invocationContext);
    }
}
