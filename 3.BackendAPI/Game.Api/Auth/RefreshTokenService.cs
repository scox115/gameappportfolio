using System.Security.Cryptography;
using System.Text;
using Game.Api.Options;
using Game.Infrastructure.Data;
using Game.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Game.Api.Auth;

public record IssuedRefreshToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// The result of presenting a refresh token. <see cref="Replacement"/> is null when the token
/// was revoked because the player signed in somewhere else (or signed out).
/// </summary>
public record RefreshRotation(Guid UserId, IssuedRefreshToken? Replacement);

/// <summary>
/// Issues, rotates and revokes refresh tokens. Every refresh replaces the token, and using a
/// token that was already replaced revokes all of that user's tokens, because it means a copy
/// of the token is in someone else's hands.
/// </summary>
public class RefreshTokenService(
    AppDbContext dbContext,
    IOptions<JwtOptions> options,
    TimeProvider timeProvider,
    ILogger<RefreshTokenService> logger)
{
    private DateTime Now => timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>Adds a new token for the user. The caller saves the DbContext.</summary>
    public IssuedRefreshToken Issue(Guid userId) => Create(userId).Issued;

    /// <summary>
    /// Uses up a refresh token and issues its replacement. Returns null when the token is unknown,
    /// expired or was already used, and a rotation without a replacement when it was revoked by a
    /// newer sign-in. The caller saves the DbContext.
    /// </summary>
    public async Task<RefreshRotation?> RotateAsync(string? token)
    {
        var existing = await FindAsync(token);
        if (existing is null) return null;

        var now = Now;
        if (existing.IsRevoked && existing.ReplacedByTokenId is null)
        {
            // Ended by a newer sign-in or a sign-out, not used: nothing suspicious.
            return new RefreshRotation(existing.UserId, null);
        }

        if (existing.IsRevoked)
        {
            // A replaced token came back: assume it was stolen and end every session for this user.
            logger.LogWarning("Refresh token reuse detected for user {UserId}; revoking all of their sessions.", existing.UserId);
            await RevokeAllAsync(existing.UserId);
            await dbContext.SaveChangesAsync();
            return null;
        }

        if (!existing.IsActive(now)) return null;

        var (replacement, issued) = Create(existing.UserId);
        existing.Revoke(now, replacement.Id);
        return new RefreshRotation(existing.UserId, issued);
    }

    /// <summary>Revokes a token, for signing out. Unknown tokens are ignored.</summary>
    public async Task RevokeAsync(string? token)
    {
        var existing = await FindAsync(token);
        if (existing is null) return;

        existing.Revoke(Now);
        await dbContext.SaveChangesAsync();
    }

    private (RefreshToken Entity, IssuedRefreshToken Issued) Create(Guid userId)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        var now = Now;
        var entity = new RefreshToken(userId, Hash(token), now, now.AddDays(options.Value.RefreshTokenDays));
        dbContext.RefreshTokens.Add(entity);
        return (entity, new IssuedRefreshToken(token, new DateTimeOffset(entity.ExpiresAt, TimeSpan.Zero)));
    }

    /// <summary>Revokes every active token the user holds. The caller saves the DbContext.</summary>
    public async Task RevokeAllAsync(Guid userId)
    {
        var now = Now;
        var active = await dbContext.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync();
        foreach (var token in active)
        {
            token.Revoke(now);
        }
    }

    private async Task<RefreshToken?> FindAsync(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var hash = Hash(token);
        return await dbContext.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash);
    }

    private static string Hash(string token) => SecureTokens.Hash(token);
}
