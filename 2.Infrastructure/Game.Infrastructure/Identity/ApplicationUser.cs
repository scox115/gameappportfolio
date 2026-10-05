using Microsoft.AspNetCore.Identity;

namespace Game.Infrastructure.Identity;

// Sign-in account managed by ASP.NET Core Identity. Its Id is also the Player's Id,
// so the token's subject claim identifies the player profile directly.
public class ApplicationUser : IdentityUser<Guid>
{
}
