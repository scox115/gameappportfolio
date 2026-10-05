using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Game.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Game.Api.Auth;

public static class SessionClaims
{
    /// <summary>The access token claim naming the sign-in session it belongs to.</summary>
    public const string SessionId = "sid";

    /// <summary>Response header telling the client why its token stopped working.</summary>
    public const string EndedHeader = "X-Session-Ended";

    public const string SignedInElsewhere = "signed-in-elsewhere";
}

/// <summary>
/// Checks that a token belongs to the account's latest sign-in. Signing in starts a new session,
/// which retires the tokens held by any other browser.
/// </summary>
public class ActiveSessionValidator(AppDbContext dbContext)
{
    public async Task<bool> IsCurrentAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(user.FindFirstValue(JwtRegisteredClaimNames.Sub), out var userId) ||
            !Guid.TryParse(user.FindFirstValue(SessionClaims.SessionId), out var sessionId))
        {
            return false;
        }

        return await dbContext.Users.AnyAsync(u => u.Id == userId && u.CurrentSessionId == sessionId, cancellationToken);
    }
}
