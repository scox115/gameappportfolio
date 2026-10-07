using System.Security.Claims;
using Game.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace Game.Api.Auth;

/// <summary>
/// Asks a signed-in player for their password again before a change to how they sign in, so a
/// browser left open can't be used to take over the account. Wrong passwords count toward the lockout
/// like a sign-in.
/// </summary>
public static class PasswordConfirmation
{
    public abstract record Outcome
    {
        /// <summary>The answer to send when the password wasn't confirmed.</summary>
        public IResult Refusal(string field) => this is LockedOut ? TooManyAttempts() : WrongPassword(field);
    }

    public sealed record Confirmed(ApplicationUser User) : Outcome;
    public sealed record Wrong : Outcome;
    public sealed record LockedOut : Outcome;

    public static async Task<Outcome> CheckAsync(
        ClaimsPrincipal user, string? password, UserManager<ApplicationUser> userManager, SignInManager<ApplicationUser> signInManager)
    {
        if (await userManager.FindByIdAsync(user.GetPlayerId().ToString()) is not { } account) return new Wrong();
        var check = await signInManager.CheckPasswordSignInAsync(account, password ?? string.Empty, lockoutOnFailure: true);
        if (check.IsLockedOut) return new LockedOut();
        return check.Succeeded ? new Confirmed(account) : new Wrong();
    }

    public static IResult WrongPassword(string field) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = ["That password isn't right."] });

    public static IResult TooManyAttempts() =>
        Results.Problem("Too many failed attempts. Try again later.", statusCode: StatusCodes.Status423Locked);
}
