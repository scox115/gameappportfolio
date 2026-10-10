using System.Net.Mail;
using Game.Api.Auth;
using Game.Api.Hubs;
using Game.Api.Options;
using Game.Core.Interfaces;
using Game.Infrastructure.Data;
using Game.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Game.Api.Recovery;

/// <param name="Email">The confirmed address reset links go to, if any.</param>
/// <param name="Pending">An address waiting for its confirmation link to be clicked, if any.</param>
public record RecoveryEmailResponse(string? Email, string? Pending);

public enum EmailChangeResult { Sent, InvalidAddress, TooSoon }

public enum ResetResult { Done, InvalidLink, WeakPassword }

/// <summary>
/// Account recovery: a player adds an email address and confirms it, and can then ask for a link
/// to choose a new password. See docs/adr/0022-account-recovery-by-email.md.
/// </summary>
public class AccountRecoveryService(
    AppDbContext dbContext,
    UserManager<ApplicationUser> userManager,
    IEmailSender emailSender,
    IOptions<EmailOptions> options,
    RefreshTokenService refreshTokens,
    SessionNotifier notifier,
    TimeProvider timeProvider,
    ILogger<AccountRecoveryService> logger)
{
    private readonly EmailOptions _options = options.Value;
    private DateTime Now => timeProvider.GetUtcNow().UtcDateTime;

    public async Task<RecoveryEmailResponse> GetEmailAsync(ApplicationUser user, CancellationToken cancellationToken = default)
    {
        var now = Now;
        var pending = await dbContext.AccountTokens.AsNoTracking()
            .Where(t => t.UserId == user.Id && t.Purpose == AccountTokenPurpose.ConfirmEmail && t.UsedAt == null && t.ExpiresAt > now)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => t.Email)
            .FirstOrDefaultAsync(cancellationToken);
        return new RecoveryEmailResponse(user.EmailConfirmed ? user.Email : null, pending);
    }

    /// <summary>Sends a confirmation link to a new address. The address is only used once it's confirmed.</summary>
    public async Task<EmailChangeResult> RequestEmailAsync(ApplicationUser user, string? address, CancellationToken cancellationToken = default)
    {
        if (Normalize(address) is not { } email) return EmailChangeResult.InvalidAddress;
        if (await SentRecentlyAsync(user.Id, AccountTokenPurpose.ConfirmEmail, cancellationToken)) return EmailChangeResult.TooSoon;

        await RetireAsync(user.Id, AccountTokenPurpose.ConfirmEmail, cancellationToken);
        var token = Issue(user.Id, AccountTokenPurpose.ConfirmEmail, _options.ConfirmLinkLifetime, email);
        await dbContext.SaveChangesAsync(cancellationToken);

        await emailSender.SendAsync(
            RecoveryEmails.ConfirmEmail(email, user.UserName!, Link("confirm-email", user.Id, token), _options.ConfirmLinkLifetime),
            cancellationToken);
        logger.LogInformation("Sent an email confirmation link to player {PlayerId}.", user.Id);
        return EmailChangeResult.Sent;
    }

    public const string NotAnAddress = "That isn't an email address we can send to.";

    /// <summary>
    /// The optional address given when a hero is created or a guest is kept: null when there's none to use,
    /// because it was left blank or email isn't set up here.
    /// </summary>
    public string? OptionalAddress(string? address) => _options.Enabled && !string.IsNullOrWhiteSpace(address) ? address : null;

    /// <summary>
    /// Sends the first confirmation link for an address given with a new or kept hero. The hero is already
    /// saved, so a link that can't be sent is logged rather than undoing that; the address can be added again
    /// from the account dialog.
    /// </summary>
    public async Task OfferConfirmationAsync(ApplicationUser user, string address)
    {
        try
        {
            await RequestEmailAsync(user, address);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't send the email confirmation link to player {PlayerId}.", user.Id);
        }
    }

    /// <summary>Removes the recovery address, and cancels any confirmation still waiting.</summary>
    public async Task RemoveEmailAsync(ApplicationUser user, CancellationToken cancellationToken = default)
    {
        await RetireAsync(user.Id, AccountTokenPurpose.ConfirmEmail, cancellationToken);
        await RetireAsync(user.Id, AccountTokenPurpose.ResetPassword, cancellationToken);
        user.Email = null;
        user.NormalizedEmail = null;
        user.EmailConfirmed = false;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Confirms the address a link was sent to, and returns it; null when the link is no good.</summary>
    public async Task<string?> ConfirmEmailAsync(Guid userId, string? token, CancellationToken cancellationToken = default)
    {
        if (await FindUsableAsync(userId, AccountTokenPurpose.ConfirmEmail, token, cancellationToken) is not { } link) return null;
        if (await userManager.FindByIdAsync(userId.ToString()) is not { } user) return null;

        link.Use(Now);
        user.Email = link.Email;
        user.NormalizedEmail = userManager.NormalizeEmail(link.Email);
        user.EmailConfirmed = true;
        // Reset links sent to an older address stop working.
        await RetireAsync(userId, AccountTokenPurpose.ResetPassword, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Player {PlayerId} confirmed a recovery email.", userId);
        return user.Email;
    }

    /// <summary>
    /// Emails a reset link when the hero exists and has a confirmed address. Says nothing either way,
    /// so the answer can't be used to find out which heroes exist or have an address.
    /// </summary>
    public async Task SendResetLinkAsync(string? username, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username)) return;
        var user = await userManager.FindByNameAsync(username.Trim());
        if (user is not { EmailConfirmed: true, Email: { } email }) return;
        if (await SentRecentlyAsync(user.Id, AccountTokenPurpose.ResetPassword, cancellationToken)) return;

        await RetireAsync(user.Id, AccountTokenPurpose.ResetPassword, cancellationToken);
        var token = Issue(user.Id, AccountTokenPurpose.ResetPassword, _options.ResetLinkLifetime);
        await dbContext.SaveChangesAsync(cancellationToken);

        await emailSender.SendAsync(
            RecoveryEmails.ResetPassword(email, user.UserName!, Link("reset-password", user.Id, token), _options.ResetLinkLifetime),
            cancellationToken);
        logger.LogInformation("Sent a password reset link to player {PlayerId}.", user.Id);
    }

    /// <summary>
    /// Sets a new password from a reset link. Signs the hero out everywhere, since the reset may be
    /// because someone else knew the old password, and clears any lockout from failed guesses.
    /// </summary>
    public async Task<(ResetResult Result, IEnumerable<string> Errors)> ResetPasswordAsync(
        Guid userId, string? token, string? newPassword, CancellationToken cancellationToken = default)
    {
        if (await FindUsableAsync(userId, AccountTokenPurpose.ResetPassword, token, cancellationToken) is not { } link
            || await userManager.FindByIdAsync(userId.ToString()) is not { } user)
        {
            return (ResetResult.InvalidLink, []);
        }

        var password = newPassword ?? string.Empty;
        var errors = new List<string>();
        foreach (var validator in userManager.PasswordValidators)
        {
            var check = await validator.ValidateAsync(userManager, user, password);
            errors.AddRange(check.Errors.Select(e => e.Description));
        }
        if (errors.Count > 0) return (ResetResult.WeakPassword, errors);

        link.Use(Now);
        user.PasswordHash = userManager.PasswordHasher.HashPassword(user, password);
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;
        user.CurrentSessionId = null;
        await refreshTokens.RevokeAllAsync(userId);
        // Saves everything above in one go, and changes the security stamp.
        var saved = await userManager.UpdateSecurityStampAsync(user);
        if (!saved.Succeeded)
        {
            throw new InvalidOperationException($"Couldn't save the new password: {string.Join(" ", saved.Errors.Select(e => e.Description))}");
        }

        await notifier.EndOtherSessionsAsync(userId);
        logger.LogInformation("Player {PlayerId} reset their password.", userId);
        return (ResetResult.Done, []);
    }

    /// <summary>An email address as it will be stored, or null when it isn't one.</summary>
    public static string? Normalize(string? address)
    {
        var trimmed = address?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > AccountToken.EmailMaxLength) return null;
        if (!MailAddress.TryCreate(trimmed, out var parsed) || parsed.Address != trimmed || !string.IsNullOrEmpty(parsed.DisplayName)) return null;
        // A bare "name@localhost" is valid to MailAddress but can't be delivered from Azure.
        return parsed.Host.Contains('.') && !parsed.Host.EndsWith('.') ? trimmed : null;
    }

    private string Issue(Guid userId, AccountTokenPurpose purpose, TimeSpan lifetime, string? email = null)
    {
        var token = SecureTokens.New();
        dbContext.AccountTokens.Add(new AccountToken(userId, purpose, SecureTokens.Hash(token), Now, lifetime, email));
        return token;
    }

    private Uri Link(string page, Guid userId, string token) =>
        new($"{_options.ClientBaseUrl!.TrimEnd('/')}/{page}?user={userId}&token={token}");

    private async Task<AccountToken?> FindUsableAsync(Guid userId, AccountTokenPurpose purpose, string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var hash = SecureTokens.Hash(token);
        var link = await dbContext.AccountTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        return link is not null && link.UserId == userId && link.Purpose == purpose && link.IsUsable(Now) ? link : null;
    }

    private async Task<bool> SentRecentlyAsync(Guid userId, AccountTokenPurpose purpose, CancellationToken cancellationToken)
    {
        var since = Now - _options.ResendCooldown;
        return await dbContext.AccountTokens.AnyAsync(t => t.UserId == userId && t.Purpose == purpose && t.CreatedAt > since, cancellationToken);
    }

    // Only the newest link of each kind works.
    private async Task RetireAsync(Guid userId, AccountTokenPurpose purpose, CancellationToken cancellationToken)
    {
        var now = Now;
        foreach (var old in await dbContext.AccountTokens.Where(t => t.UserId == userId && t.Purpose == purpose && t.UsedAt == null).ToListAsync(cancellationToken))
        {
            old.Use(now);
        }
    }
}
