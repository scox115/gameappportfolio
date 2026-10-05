using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Game.Api.Auth;

public static class ClaimsPrincipalExtensions
{
    // The token subject is the account id, which is also the player id.
    public static Guid GetPlayerId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(JwtRegisteredClaimNames.Sub), out var id)
            ? id
            : throw new InvalidOperationException("Authenticated user has no valid subject claim.");
}
