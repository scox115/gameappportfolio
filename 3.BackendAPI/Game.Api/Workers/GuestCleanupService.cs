using Game.Api.Accounts;
using Game.Api.Observability;
using Game.Api.Options;
using Game.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Game.Api.Workers;

/// <summary>
/// Deletes guest heroes nobody kept, once nobody can play them any more: started more than
/// <see cref="CleanupOptions.GuestRetentionDays"/> ago and with no refresh token that still works. A guest
/// has no password, so once its last session is gone it can never be signed in to again.
/// Each is deleted like an account its player deleted (see docs/adr/0030-guest-play.md).
/// </summary>
public class GuestCleanupService(
    AppDbContext dbContext,
    AccountService accounts,
    TimeProvider timeProvider,
    IOptions<CleanupOptions> options,
    ILogger<GuestCleanupService> logger)
{
    private readonly CleanupOptions _options = options.Value;

    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var startedBefore = now.AddDays(-_options.GuestRetentionDays);

        var abandoned = await dbContext.Players
            .Where(p => p.GuestSince < startedBefore)
            .Where(p => !dbContext.RefreshTokens.Any(t => t.UserId == p.Id && t.RevokedAt == null && t.ExpiresAt > now))
            .OrderBy(p => p.GuestSince)
            .Select(p => p.Id)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);

        var deleted = 0;
        foreach (var playerId in abandoned)
        {
            dbContext.ChangeTracker.Clear(); // each deletion is its own save
            try
            {
                // One still in a duel is left for the next run.
                if (await accounts.DeleteAsync(playerId, cancellationToken) == DeletionResult.Deleted) deleted++;
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Couldn't delete abandoned guest {PlayerId}; the next run tries again.", playerId);
            }
        }

        GameTelemetry.RowsCleanedUp("guest", deleted);
        return deleted;
    }
}
