using System.Security.Cryptography;
using System.Text;
using Game.Core.Security;
using Game.Infrastructure.Data;
using Game.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Game.Api.TwoFactor;

public record TwoFactorStatus(bool Enabled, int RecoveryCodesLeft);

/// <param name="SharedKey">The secret, in groups of four, for typing into an app that can't scan.</param>
/// <param name="AuthenticatorUri">The otpauth:// link the QR code holds.</param>
public record AuthenticatorSetup(string SharedKey, string AuthenticatorUri);

public record RecoveryCodes(List<string> Codes);

/// <summary>
/// Two-factor sign-in with an authenticator app, plus one-time recovery codes for a lost phone.
/// The authenticator key lives where Identity keeps it (AspNetUserTokens); recovery codes are stored
/// only as hashes, and a code from the app is accepted once. See docs/adr/0025-two-factor-sign-in.md.
/// </summary>
public sealed class TwoFactorService(
    UserManager<ApplicationUser> userManager,
    AppDbContext dbContext,
    TimeProvider timeProvider)
{
    public const string Issuer = "Kings of the Card Arena";
    public const int RecoveryCodeCount = 10;

    // Identity's own name for the authenticator key's row, and this game's for the recovery code hashes.
    private const string IdentityProvider = "[AspNetUserStore]";
    private const string KeyName = "AuthenticatorKey";
    private const string GameProvider = "[CardArena]";
    private const string RecoveryCodesName = "RecoveryCodeHashes";

    // No 0/O or 1/I/L, so a code copied from paper reads back the same.
    private const string RecoveryAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public async Task<TwoFactorStatus> StatusAsync(ApplicationUser user, CancellationToken cancellationToken = default) =>
        new(user.TwoFactorEnabled, user.TwoFactorEnabled ? (await RecoveryHashesAsync(user, cancellationToken)).Count : 0);

    /// <summary>A new secret for the player's app. Two-factor isn't on until they prove it works with <see cref="EnableAsync"/>.</summary>
    public async Task<AuthenticatorSetup> BeginSetupAsync(ApplicationUser user)
    {
        await userManager.ResetAuthenticatorKeyAsync(user);
        var key = (await userManager.GetAuthenticatorKeyAsync(user))!;
        var label = Uri.EscapeDataString($"{Issuer}:{user.UserName}");
        var uri = $"otpauth://totp/{label}?secret={key}&issuer={Uri.EscapeDataString(Issuer)}&digits={Totp.Digits}&period={(int)Totp.StepLength.TotalSeconds}";
        return new AuthenticatorSetup(string.Join(' ', key.Chunk(4).Select(c => new string(c))), uri);
    }

    /// <summary>Turns two-factor on once the app shows a right code, and returns the first recovery codes; null for a wrong code.</summary>
    public async Task<RecoveryCodes?> EnableAsync(ApplicationUser user, string? code, CancellationToken cancellationToken = default)
    {
        if (await MatchAuthenticatorAsync(user, code) is not { } step) return null;

        user.UsedTwoFactorStep(step);
        user.TwoFactorEnabled = true;
        var codes = await ReplaceRecoveryCodesAsync(user, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return codes;
    }

    /// <summary>
    /// Checks a second-step code: six digits from the app, or one of the recovery codes (used up by this).
    /// Saves when it is right, so the same code can't be used again.
    /// </summary>
    public async Task<bool> VerifyAsync(ApplicationUser user, string? code, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code) || !user.TwoFactorEnabled) return false;

        if (await MatchAuthenticatorAsync(user, code) is { } step)
        {
            user.UsedTwoFactorStep(step);
        }
        else
        {
            var hashes = await RecoveryHashesAsync(user, cancellationToken);
            if (!hashes.Remove(Hash(code))) return false;
            await SetRecoveryHashesAsync(user, hashes, cancellationToken);
        }

        // A new concurrency stamp makes two sign-ins racing with one code save only once.
        user.ConcurrencyStamp = Guid.NewGuid().ToString();
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }

    /// <summary>New recovery codes; the old ones stop working.</summary>
    public async Task<RecoveryCodes> NewRecoveryCodesAsync(ApplicationUser user, CancellationToken cancellationToken = default)
    {
        var codes = await ReplaceRecoveryCodesAsync(user, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return codes;
    }

    /// <summary>
    /// Turns two-factor off and forgets the key and recovery codes. Staged only: the caller saves,
    /// so an admin's audit entry goes in the same save.
    /// </summary>
    public async Task TurnOffAsync(ApplicationUser user, CancellationToken cancellationToken = default)
    {
        user.TwoFactorEnabled = false;
        dbContext.UserTokens.RemoveRange(await dbContext.UserTokens
            .Where(t => t.UserId == user.Id &&
                        ((t.LoginProvider == IdentityProvider && t.Name == KeyName) ||
                         (t.LoginProvider == GameProvider && t.Name == RecoveryCodesName)))
            .ToListAsync(cancellationToken));
    }

    private async Task<long?> MatchAuthenticatorAsync(ApplicationUser user, string? code)
    {
        if (await userManager.GetAuthenticatorKeyAsync(user) is not { } key) return null;
        return Totp.Match(Base32.Decode(key), code, timeProvider.GetUtcNow(), user.LastTwoFactorStep);
    }

    private async Task<RecoveryCodes> ReplaceRecoveryCodesAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        var codes = Enumerable.Range(0, RecoveryCodeCount).Select(_ => NewRecoveryCode()).ToList();
        await SetRecoveryHashesAsync(user, codes.Select(Hash).ToList(), cancellationToken);
        return new RecoveryCodes(codes);
    }

    private static string NewRecoveryCode()
    {
        var code = RandomNumberGenerator.GetString(RecoveryAlphabet, 10);
        return $"{code[..5]}-{code[5..]}";
    }

    // The codes are 10 random characters (about 50 bits), too many to guess, so a plain SHA-256 is enough.
    private static string Hash(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code.Replace("-", "").Replace(" ", "").Trim().ToUpperInvariant())));

    private async Task<List<string>> RecoveryHashesAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        var row = await RecoveryRowAsync(user, cancellationToken);
        return row?.Value is { Length: > 0 } value ? value.Split(';').ToList() : [];
    }

    private async Task SetRecoveryHashesAsync(ApplicationUser user, List<string> hashes, CancellationToken cancellationToken)
    {
        var row = await RecoveryRowAsync(user, cancellationToken);
        if (row is null)
        {
            row = new IdentityUserToken<Guid> { UserId = user.Id, LoginProvider = GameProvider, Name = RecoveryCodesName };
            dbContext.UserTokens.Add(row);
        }
        row.Value = string.Join(';', hashes);
    }

    private async Task<IdentityUserToken<Guid>?> RecoveryRowAsync(ApplicationUser user, CancellationToken cancellationToken) =>
        dbContext.UserTokens.Local.FirstOrDefault(Matches(user)) ??
        await dbContext.UserTokens.FirstOrDefaultAsync(t => t.UserId == user.Id && t.LoginProvider == GameProvider && t.Name == RecoveryCodesName, cancellationToken);

    private static Func<IdentityUserToken<Guid>, bool> Matches(ApplicationUser user) =>
        t => t.UserId == user.Id && t.LoginProvider == GameProvider && t.Name == RecoveryCodesName;
}
