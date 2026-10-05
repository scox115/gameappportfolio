using Microsoft.AspNetCore.Identity;

namespace Game.Infrastructure.Identity;

// Sign-in account managed by ASP.NET Core Identity. Its Id is also the Player's Id,
// so the token's subject claim identifies the player profile directly.
public class ApplicationUser : IdentityUser<Guid>
{
    /// <summary>
    /// The session started by the most recent sign-in. Access tokens carry it, and a token from
    /// an older session is refused, so an account is only signed in on one browser at a time.
    /// </summary>
    public Guid? CurrentSessionId { get; set; }
}
