using Game.Api.Options;
using Game.Api.Workers;
using Game.Core.Admin;
using Game.Core.Battles;
using Game.Infrastructure.Data;
using Game.Infrastructure.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Game.Api.Tests;

// The cleanup uses bulk deletes (ExecuteDeleteAsync), which the in-memory provider can't run,
// so these tests use an in-memory SQLite database, a real relational engine.
public sealed class DataCleanupTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly TestClock _clock = new();
    private readonly Guid _userId = Guid.NewGuid();

    public DataCleanupTests()
    {
        _connection.Open();
        using var db = NewContext();
        db.Database.EnsureCreated();
        db.Users.Add(new ApplicationUser { Id = _userId, UserName = "cleaner" });
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    [Fact]
    public async Task RefreshTokens_AreDeletedOnceTheyAreLongExpired()
    {
        var longExpired = Token(createdDaysAgo: 30, livesForDays: 7);   // expired 23 days ago
        var recentlyExpired = Token(createdDaysAgo: 10, livesForDays: 7); // expired 3 days ago
        var active = Token(createdDaysAgo: 1, livesForDays: 7);
        await SaveAsync(longExpired, recentlyExpired, active);

        var result = await RunCleanupAsync();

        Assert.Equal(1, result.ExpiredRefreshTokens);
        await using var db = NewContext();
        Assert.Equal(
            new[] { recentlyExpired.Id, active.Id }.Order(),
            (await db.RefreshTokens.Select(t => t.Id).ToListAsync()).Order());
    }

    [Fact]
    public async Task OldFinishedBattles_AreDeleted_ButRecentAndUnfinishedOnesStay()
    {
        var oldBossFight = FinishedBossFight(daysAgo: 45);
        var recentBossFight = FinishedBossFight(daysAgo: 5);
        var abandonedBossFight = PveBattle.Start(_userId, Now.AddDays(-90)); // waits for its player to come back
        var oldDuel = FinishedDuel(daysAgo: 31);
        var recentDuel = FinishedDuel(daysAgo: 1);
        await SaveAsync(oldBossFight, recentBossFight, abandonedBossFight, oldDuel, recentDuel);

        var result = await RunCleanupAsync();

        Assert.Equal(new CleanupResult(0, 1, 1), result);
        await using var db = NewContext();
        Assert.Equal(
            new[] { recentBossFight.Id, abandonedBossFight.Id }.Order(),
            (await db.PveBattles.Select(b => b.Id).ToListAsync()).Order());
        Assert.Equal([recentDuel.Id], await db.PvpBattles.Select(b => b.Id).ToListAsync());
    }

    [Fact]
    public async Task EmailLinks_AreDeletedOnceTheyAreLongExpired()
    {
        var old = new AccountToken(_userId, AccountTokenPurpose.ResetPassword, "old-hash", Now.AddDays(-10), TimeSpan.FromHours(1));
        var recent = new AccountToken(_userId, AccountTokenPurpose.ConfirmEmail, "recent-hash", Now.AddDays(-2), TimeSpan.FromDays(1), "a@example.com");
        await SaveAsync(old, recent);

        var result = await RunCleanupAsync();

        Assert.Equal(1, result.ExpiredEmailLinks);
        await using var db = NewContext();
        Assert.Equal([recent.Id], await db.AccountTokens.Select(t => t.Id).ToListAsync());
    }

    [Fact]
    public async Task AuditLogEntries_AreKeptForAYear()
    {
        var old = AuditEntry(daysAgo: 400);
        var recent = AuditEntry(daysAgo: 300);
        await SaveAsync(old, recent);

        var result = await RunCleanupAsync();

        Assert.Equal(1, result.OldAuditEntries);
        await using var db = NewContext();
        Assert.Equal([recent.Id], await db.AuditLog.Select(e => e.Id).ToListAsync());
    }

    [Fact]
    public async Task ABigBacklog_IsDeletedInBatches()
    {
        await SaveAsync(Enumerable.Range(0, 25).Select(_ => (object)Token(createdDaysAgo: 60, livesForDays: 7)).ToArray());

        var result = await RunCleanupAsync(batchSize: 10);

        Assert.Equal(25, result.ExpiredRefreshTokens);
        await using var db = NewContext();
        Assert.Empty(db.RefreshTokens);
    }

    [Fact]
    public async Task ASecondRun_HasNothingLeftToDelete()
    {
        await SaveAsync(Token(createdDaysAgo: 60, livesForDays: 7), FinishedBossFight(daysAgo: 60));
        await RunCleanupAsync();

        Assert.Equal(0, (await RunCleanupAsync()).Total);
    }

    private AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);

    private async Task<CleanupResult> RunCleanupAsync(int batchSize = 1_000)
    {
        await using var db = NewContext();
        var options = Microsoft.Extensions.Options.Options.Create(new CleanupOptions { BatchSize = batchSize });
        return await new DataCleanupService(db, _clock, options).RunAsync();
    }

    private async Task SaveAsync(params object[] entities)
    {
        await using var db = NewContext();
        db.AddRange(entities);
        await db.SaveChangesAsync();
    }

    private RefreshToken Token(int createdDaysAgo, int livesForDays)
    {
        var created = Now.AddDays(-createdDaysAgo);
        return new RefreshToken(_userId, Guid.NewGuid().ToString("N"), created, created.AddDays(livesForDays));
    }

    private PveBattle FinishedBossFight(int daysAgo)
    {
        var at = Now.AddDays(-daysAgo);
        var battle = PveBattle.Start(_userId, at);
        var random = new FixedBattleRandom();
        while (!battle.IsFinished)
        {
            var card = Enum.GetValues<BattleCard>().First(battle.CanPlay);
            battle.PlayCard(card, random, at);
        }
        return battle;
    }

    private AuditLogEntry AuditEntry(int daysAgo) =>
        new(Now.AddDays(-daysAgo), AdminAction.AdjustGold, Guid.NewGuid(), "admin", _userId, "cleaner", "Refund for a bug.", "+10 gold");

    private PvpBattle FinishedDuel(int daysAgo)
    {
        var at = Now.AddDays(-daysAgo);
        var battle = PvpBattle.Start(_userId, Guid.NewGuid(), at);
        battle.Forfeit(_userId, at);
        return battle;
    }
}
