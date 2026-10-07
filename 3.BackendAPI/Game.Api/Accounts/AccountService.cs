using Game.Api.Endpoints;
using Game.Api.Hubs;
using Game.Core.Battles;
using Game.Core.Interfaces;
using Game.Infrastructure.Data;
using Game.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Game.Api.Accounts;

public enum DeletionResult
{
    Deleted,
    NotFound,

    /// <summary>The player is in a duel; deleting now would leave their opponent stuck.</summary>
    InDuel,
}

/// <summary>
/// A player's right to their data: a copy of everything stored about them, and deleting it all.
/// See docs/adr/0020-account-export-and-deletion.md for what is kept and why.
/// </summary>
public class AccountService(
    AppDbContext dbContext,
    UserManager<ApplicationUser> userManager,
    IStorageService storage,
    PvpMatchmaker matchmaker,
    TimeProvider timeProvider,
    ILogger<AccountService> logger)
{
    /// <summary>Everything stored about the player, or null if they don't exist.</summary>
    public async Task<AccountExport?> ExportAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        var user = await userManager.FindByIdAsync(playerId.ToString());
        var player = await dbContext.Players.FindAsync([playerId], cancellationToken);
        if (user is null || player is null) return null;

        var sessions = await dbContext.RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == playerId)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new AccountExport.SignInSession(t.CreatedAt, t.ExpiresAt, t.RevokedAt))
            .ToListAsync(cancellationToken);

        var history = await dbContext.MatchHistory.AsNoTracking()
            .Where(e => e.PlayerId == playerId)
            .OrderByDescending(e => e.PlayedAt)
            .ToListAsync(cancellationToken);

        var bossFights = await dbContext.PveBattles.AsNoTracking()
            .Where(b => b.PlayerId == playerId)
            .OrderByDescending(b => b.StartedAt)
            .Select(b => new AccountExport.BossFight(b.Id, b.Difficulty, b.Class, b.Status, b.Turn, b.StartedAt, b.CompletedAt))
            .ToListAsync(cancellationToken);

        var duels = await dbContext.PvpBattles.AsNoTracking()
            .Where(b => b.PlayerOneId == playerId || b.PlayerTwoId == playerId)
            .OrderByDescending(b => b.StartedAt)
            .ToListAsync(cancellationToken);

        return new AccountExport(
            ExportedAt: timeProvider.GetUtcNow(),
            Account: new AccountExport.SignInAccount(user.Id, user.UserName ?? player.Username, user.LockoutEnd, user.AccessFailedCount),
            Profile: AccountExport.HeroProfile.From(player),
            SignInSessions: sessions,
            MatchHistory: history.Select(AccountExport.Match.From).ToList(),
            BossFights: bossFights,
            Duels: duels.Select(d => AccountExport.Duel.From(d, playerId)).ToList());
    }

    /// <summary>Deletes the account, the hero and everything recorded about them.</summary>
    public async Task<DeletionResult> DeleteAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        var user = await userManager.FindByIdAsync(playerId.ToString());
        var player = await dbContext.Players.FindAsync([playerId], cancellationToken);
        if (user is null || player is null) return DeletionResult.NotFound;

        if (await dbContext.PvpBattles.AnyAsync(b =>
                (b.PlayerOneId == playerId || b.PlayerTwoId == playerId) && b.Status == PvpBattleStatus.InProgress, cancellationToken))
        {
            return DeletionResult.InDuel;
        }

        matchmaker.Leave(playerId);

        // Loaded and removed one by one rather than with ExecuteDelete, so everything goes in the one
        // SaveChanges that UserManager.DeleteAsync makes: the account is deleted completely or not at all.
        dbContext.RefreshTokens.RemoveRange(await dbContext.RefreshTokens.Where(t => t.UserId == playerId).ToListAsync(cancellationToken));
        dbContext.PveBattles.RemoveRange(await dbContext.PveBattles.Where(b => b.PlayerId == playerId).ToListAsync(cancellationToken));
        dbContext.PvpBattles.RemoveRange(await dbContext.PvpBattles
            .Where(b => b.PlayerOneId == playerId || b.PlayerTwoId == playerId).ToListAsync(cancellationToken));
        dbContext.Matches.RemoveRange(await dbContext.Matches
            .Where(m => m.PlayerOneId == playerId || m.PlayerTwoId == playerId).ToListAsync(cancellationToken));
        dbContext.MatchHistory.RemoveRange(await dbContext.MatchHistory.Where(e => e.PlayerId == playerId).ToListAsync(cancellationToken));

        // Opponents keep their own record of the match, without this hero's name.
        foreach (var entry in await dbContext.MatchHistory.Where(e => e.OpponentId == playerId).ToListAsync(cancellationToken))
        {
            entry.RetireOpponent();
        }

        var avatarUrl = player.AvatarUrl;
        dbContext.Players.Remove(player);

        var result = await userManager.DeleteAsync(user); // saves every change above
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Couldn't delete account {playerId}: {string.Join(" ", result.Errors.Select(e => e.Description))}");
        }

        // The portrait goes last: if this fails the account is still gone, and soft delete removes
        // the blob for good after 7 days anyway.
        if (avatarUrl is not null)
        {
            try
            {
                await storage.DeleteFileAsync(avatarUrl, PlayerEndpoints.AvatarContainer);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Couldn't delete the portrait of deleted player {PlayerId}: {Error}", playerId, ex.Message);
            }
        }

        logger.LogInformation("Player {PlayerId} deleted their account.", playerId);
        return DeletionResult.Deleted;
    }
}
