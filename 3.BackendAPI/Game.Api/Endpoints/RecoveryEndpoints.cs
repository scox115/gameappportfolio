using System.Security.Claims;
using Game.Api.Auth;
using Game.Api.Options;
using Game.Api.Recovery;
using Game.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Game.Api.Endpoints;

/// <param name="Email">The new address; a confirmation link is sent to it.</param>
public record RecoveryEmailRequest(string? Email, string? Password);

public record PasswordRequest(string? Password);

public record ForgotPasswordRequest(string? Username);

public record EmailLinkRequest(Guid UserId, string? Token);

public record ResetPasswordRequest(Guid UserId, string? Token, string? NewPassword);

// Account recovery by email: add and confirm an address, then use it to reset a forgotten password.
// See docs/adr/0022-account-recovery-by-email.md.
public static class RecoveryEndpoints
{
    public static void MapRecoveryEndpoints(this IEndpointRouteBuilder app)
    {
        var mine = app.MapGroup("/players/me/recovery-email")
                      .WithTags("Account recovery")
                      .RequireAuthorization()
                      .AddEndpointFilter(OnlyWhenEmailIsSetUp);

        mine.MapGet("", async (ClaimsPrincipal user, UserManager<ApplicationUser> userManager, AccountRecoveryService recovery, CancellationToken cancellationToken) =>
            await userManager.FindByIdAsync(user.GetPlayerId().ToString()) is { } account
                ? Results.Ok(await recovery.GetEmailAsync(account, cancellationToken))
                : Results.NotFound());

        // PUT: sends a confirmation link to the new address; the old one keeps working until it's confirmed.
        mine.MapPut("", async (
            ClaimsPrincipal user,
            RecoveryEmailRequest request,
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            AccountRecoveryService recovery,
            CancellationToken cancellationToken) =>
        {
            if (await CheckPasswordAsync(user, request.Password, userManager, signInManager) is not { } account)
                return WrongPassword(nameof(RecoveryEmailRequest.Password));
            if (account is ApplicationUserLockedOut) return LockedOut();

            return await recovery.RequestEmailAsync(account.User, request.Email, cancellationToken) switch
            {
                EmailChangeResult.Sent => Results.Accepted(value: await recovery.GetEmailAsync(account.User, cancellationToken)),
                EmailChangeResult.TooSoon => Results.Problem("A link was sent a moment ago. Check your inbox, or try again in a couple of minutes.", statusCode: StatusCodes.Status429TooManyRequests),
                _ => Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(RecoveryEmailRequest.Email)] = ["That isn't an email address we can send to."] }),
            };
        }).RequireRateLimiting(RateLimits.Recovery);

        mine.MapDelete("", async (
            ClaimsPrincipal user,
            [FromBody] PasswordRequest request,
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            AccountRecoveryService recovery,
            CancellationToken cancellationToken) =>
        {
            if (await CheckPasswordAsync(user, request.Password, userManager, signInManager) is not { } account)
                return WrongPassword(nameof(PasswordRequest.Password));
            if (account is ApplicationUserLockedOut) return LockedOut();

            await recovery.RemoveEmailAsync(account.User, cancellationToken);
            return Results.NoContent();
        }).RequireRateLimiting(RateLimits.SignIn);

        var links = app.MapGroup("/auth")
                       .WithTags("Account recovery")
                       .AllowAnonymous()
                       .AddEndpointFilter(OnlyWhenEmailIsSetUp);

        // Always 202, whether or not the hero exists or has an address, so it can't be used to probe names.
        links.MapPost("/forgot-password", async (ForgotPasswordRequest request, AccountRecoveryService recovery, CancellationToken cancellationToken) =>
        {
            await recovery.SendResetLinkAsync(request.Username, cancellationToken);
            return Results.Accepted();
        }).RequireRateLimiting(RateLimits.Recovery);

        links.MapPost("/reset-password", async (ResetPasswordRequest request, AccountRecoveryService recovery, CancellationToken cancellationToken) =>
        {
            var (result, errors) = await recovery.ResetPasswordAsync(request.UserId, request.Token, request.NewPassword, cancellationToken);
            return result switch
            {
                ResetResult.Done => Results.NoContent(),
                ResetResult.WeakPassword => Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(ResetPasswordRequest.NewPassword)] = errors.ToArray() }),
                _ => InvalidLink(),
            };
        }).RequireRateLimiting(RateLimits.SignIn);

        links.MapPost("/confirm-email", async (EmailLinkRequest request, AccountRecoveryService recovery, CancellationToken cancellationToken) =>
            await recovery.ConfirmEmailAsync(request.UserId, request.Token, cancellationToken) is { } email
                ? Results.Ok(new { email })
                : InvalidLink())
            .RequireRateLimiting(RateLimits.SignIn);
    }

    private static async ValueTask<object?> OnlyWhenEmailIsSetUp(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        context.HttpContext.RequestServices.GetRequiredService<IOptions<EmailOptions>>().Value.Enabled
            ? await next(context)
            : Results.Problem("Account recovery by email isn't available here.", statusCode: StatusCodes.Status503ServiceUnavailable);

    private record ApplicationUserChecked(ApplicationUser User);
    private sealed record ApplicationUserLockedOut(ApplicationUser User) : ApplicationUserChecked(User);

    // Counts toward the lockout like a sign-in, so a stolen session can't guess its way to changing the address.
    private static async Task<ApplicationUserChecked?> CheckPasswordAsync(
        ClaimsPrincipal user, string? password, UserManager<ApplicationUser> userManager, SignInManager<ApplicationUser> signInManager)
    {
        if (await userManager.FindByIdAsync(user.GetPlayerId().ToString()) is not { } account) return null;
        var check = await signInManager.CheckPasswordSignInAsync(account, password ?? string.Empty, lockoutOnFailure: true);
        if (check.IsLockedOut) return new ApplicationUserLockedOut(account);
        return check.Succeeded ? new ApplicationUserChecked(account) : null;
    }

    private static IResult WrongPassword(string field) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = ["That password isn't right."] });

    private static IResult LockedOut() =>
        Results.Problem("Too many failed attempts. Try again later.", statusCode: StatusCodes.Status423Locked);

    private static IResult InvalidLink() =>
        Results.Problem("This link has expired or was already used. Ask for a new one.", statusCode: StatusCodes.Status400BadRequest);
}
