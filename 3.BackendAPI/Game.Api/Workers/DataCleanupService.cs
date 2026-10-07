using Game.Api.Observability;
using Game.Api.Options;
using Game.Core.Battles;
using Game.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Game.Api.Workers;

public record CleanupResult(int ExpiredRefreshTokens, int FinishedBossFights, int FinishedDuels, int OldAuditEntries = 0, int ExpiredEmailLinks = 0)
{
    public int Total => ExpiredRefreshTokens + FinishedBossFights + FinishedDuels + OldAuditEntries + ExpiredEmailLinks;
}

/// <summary>
/// Deletes rows that are no longer needed: refresh tokens long past their expiry, the working
/// state of battles that finished long ago, expired email links, and admin audit log entries older than a year. Battles still in progress are never touched: an
/// unfinished boss fight waits for its player to come back, and duels are settled by
/// <see cref="PvpTurnTimeoutWorker"/>.
/// </summary>
public class DataCleanupService(AppDbContext dbContext, TimeProvider timeProvider, IOptions<CleanupOptions> options)
{
    private readonly CleanupOptions _options = options.Value;

    public async Task<CleanupResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var tokenCutoff = now.AddDays(-_options.ExpiredTokenRetentionDays);
        var battleCutoff = now.AddDays(-_options.FinishedBattleRetentionDays);
        var auditCutoff = now.AddDays(-_options.AuditLogRetentionDays);

        // Expired tokens can't be used, and revoked ones are only kept until they'd have expired
        // anyway, so reuse of a stolen token is still caught while it matters.
        var tokens = await DeleteInBatchesAsync(
            dbContext.RefreshTokens.Where(t => t.ExpiresAt < tokenCutoff).OrderBy(t => t.ExpiresAt), "refresh_token", cancellationToken);

        // Recovery links last an hour or a day; once expired they're only kept as long as refresh tokens.
        var emailLinks = await DeleteInBatchesAsync(
            dbContext.AccountTokens.Where(t => t.ExpiresAt < tokenCutoff).OrderBy(t => t.ExpiresAt), "account_token", cancellationToken);

        var bossFights = await DeleteInBatchesAsync(
            dbContext.PveBattles.Where(b => b.Status != BattleStatus.InProgress && b.CompletedAt < battleCutoff).OrderBy(b => b.CompletedAt),
            "boss_fight", cancellationToken);

        // Duels from the last day are still counted when deciding whether a rematch pays out.
        var duels = await DeleteInBatchesAsync(
            dbContext.PvpBattles.Where(b => b.Status != PvpBattleStatus.InProgress && b.CompletedAt < battleCutoff).OrderBy(b => b.CompletedAt),
            "duel", cancellationToken);

        var auditEntries = await DeleteInBatchesAsync(
            dbContext.AuditLog.Where(e => e.At < auditCutoff).OrderBy(e => e.At), "audit_log_entry", cancellationToken);

        return new CleanupResult(tokens, bossFights, duels, auditEntries, emailLinks);
    }

    // Each batch is its own DELETE TOP (n) statement and transaction, so a large backlog is cleared
    // in short steps instead of one long lock on the table.
    private async Task<int> DeleteInBatchesAsync<T>(IQueryable<T> rows, string kind, CancellationToken cancellationToken)
    {
        var total = 0;
        int deleted;
        do
        {
            deleted = await rows.Take(_options.BatchSize).ExecuteDeleteAsync(cancellationToken);
            total += deleted;
        }
        while (deleted == _options.BatchSize);

        GameTelemetry.RowsCleanedUp(kind, total);
        return total;
    }
}
