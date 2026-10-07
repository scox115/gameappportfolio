using System.Security.Claims;
using Game.Api.Auth;
using Game.Api.TwoFactor;
using Game.Infrastructure.Data;
using Game.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace Game.Api.Endpoints;

/// <param name="Code">Six digits from the authenticator app, or (where it says so) a recovery code.</param>
public record TwoFactorCodeRequest(string? Code);

public record TurnOffTwoFactorRequest(string? Password, string? Code);

// Two-factor sign-in with an authenticator app. See docs/adr/0025-two-factor-sign-in.md.
public static class TwoFactorEndpoints
{
    public static void MapTwoFactorEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/players/me/two-factor")
                       .WithTags("Two-factor sign-in")
                       .RequireAuthorization();

        group.MapGet("", async (ClaimsPrincipal user, UserManager<ApplicationUser> userManager, TwoFactorService twoFactor, CancellationToken cancellationToken) =>
            await userManager.FindByIdAsync(user.GetPlayerId().ToString()) is { } account
                ? Results.Ok(await twoFactor.StatusAsync(account, cancellationToken))
                : Results.NotFound());

        // Step 1: a new secret for the app, after the password. Two-factor stays off until step 2.
        group.MapPost("/authenticator", async (
            ClaimsPrincipal user,
            PasswordRequest request,
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            TwoFactorService twoFactor) =>
        {
            var check = await PasswordConfirmation.CheckAsync(user, request.Password, userManager, signInManager);
            if (check is not PasswordConfirmation.Confirmed account) return check.Refusal(nameof(PasswordRequest.Password));
            if (account.User.TwoFactorEnabled) return AlreadyOn();

            return Results.Ok(await twoFactor.BeginSetupAsync(account.User));
        }).RequireRateLimiting(RateLimits.SignIn);

        // Step 2: a code from the app proves it's set up; two-factor goes on and the recovery codes are shown once.
        group.MapPost("/enable", async (
            ClaimsPrincipal user,
            TwoFactorCodeRequest request,
            UserManager<ApplicationUser> userManager,
            TwoFactorService twoFactor,
            CancellationToken cancellationToken) =>
        {
            if (await userManager.FindByIdAsync(user.GetPlayerId().ToString()) is not { } account) return Results.NotFound();
            if (account.TwoFactorEnabled) return AlreadyOn();

            return await twoFactor.EnableAsync(account, request.Code, cancellationToken) is { } codes
                ? Results.Ok(codes)
                : WrongCode("That code isn't right. Check the time on your phone, then try the newest code.");
        }).RequireRateLimiting(RateLimits.SignIn);

        group.MapPost("/recovery-codes", async (
            ClaimsPrincipal user,
            TwoFactorCodeRequest request,
            UserManager<ApplicationUser> userManager,
            TwoFactorService twoFactor,
            CancellationToken cancellationToken) =>
        {
            if (await userManager.FindByIdAsync(user.GetPlayerId().ToString()) is not { } account) return Results.NotFound();
            if (!account.TwoFactorEnabled) return IsOff();
            if (!await twoFactor.VerifyAsync(account, request.Code, cancellationToken))
            {
                await userManager.AccessFailedAsync(account); // wrong codes count toward the lockout, as at sign-in
                return WrongCode();
            }

            return Results.Ok(await twoFactor.NewRecoveryCodesAsync(account, cancellationToken));
        }).RequireRateLimiting(RateLimits.SignIn);

        // Needs both the password and a code, so neither a stolen password nor a stolen phone is enough.
        group.MapPost("/disable", async (
            ClaimsPrincipal user,
            TurnOffTwoFactorRequest request,
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            TwoFactorService twoFactor,
            AppDbContext dbContext,
            CancellationToken cancellationToken) =>
        {
            var check = await PasswordConfirmation.CheckAsync(user, request.Password, userManager, signInManager);
            if (check is not PasswordConfirmation.Confirmed account) return check.Refusal(nameof(TurnOffTwoFactorRequest.Password));
            if (!account.User.TwoFactorEnabled) return IsOff();
            if (!await twoFactor.VerifyAsync(account.User, request.Code, cancellationToken))
            {
                await userManager.AccessFailedAsync(account.User);
                return WrongCode();
            }

            await twoFactor.TurnOffAsync(account.User, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        }).RequireRateLimiting(RateLimits.SignIn);
    }

    private static IResult WrongCode(string message = "That code isn't right.") =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(TwoFactorCodeRequest.Code)] = [message] });

    private static IResult AlreadyOn() =>
        Results.Problem("Two-factor sign-in is already on.", statusCode: StatusCodes.Status409Conflict);

    private static IResult IsOff() =>
        Results.Problem("Two-factor sign-in is off.", statusCode: StatusCodes.Status409Conflict);
}
