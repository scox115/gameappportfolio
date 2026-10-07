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

    /// <summary>The session ended because an admin suspended the account.</summary>
    public const string Suspended = "suspended";
}

/// <summary>
/// Checks that a token belongs to the account's latest sign-in. Signing in starts a new session,
/// which retires the tokens held by any other browser.
/// </summary>
public class ActiveSessionValidator(AppDbContext dbContext, TimeProvider timeProvider)
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

    /// <summary>
    /// Why a token that isn't current stopped working: <see cref="SessionClaims.Suspended"/> when an
    /// admin suspended the account, otherwise <see cref="SessionClaims.SignedInElsewhere"/>.
    /// </summary>
    public async Task<string> EndReasonAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var suspended = await dbContext.Users.AnyAsync(u => u.Id == userId && u.SuspendedUntil > now, cancellationToken);
        return suspended ? SessionClaims.Suspended : SessionClaims.SignedInElsewhere;
    }

    /// <inheritdoc cref="EndReasonAsync(Guid, CancellationToken)"/>
    public Task<string> EndReasonAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default) =>
        Guid.TryParse(user.FindFirstValue(JwtRegisteredClaimNames.Sub), out var userId)
            ? EndReasonAsync(userId, cancellationToken)
            : Task.FromResult(SessionClaims.SignedInElsewhere);
}
