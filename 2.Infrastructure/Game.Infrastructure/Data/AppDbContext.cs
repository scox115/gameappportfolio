using Game.Core.Admin;
using Game.Core.Battles;
using Game.Core.Entities;
using Game.Core.History;
using Game.Core.Moderation;
using Game.Core.Operations;
using Game.Infrastructure.Identity;
using Game.Infrastructure.Messaging;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using System.Reflection;

namespace Game.Infrastructure.Data;

// Identity tables (AspNetUsers, ...) live alongside the game tables in the same database.
public class AppDbContext : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<Player> Players => Set<Player>();
    public DbSet<GameMatch> Matches => Set<GameMatch>();
    public DbSet<PveBattle> PveBattles => Set<PveBattle>();
    public DbSet<PvpBattle> PvpBattles => Set<PvpBattle>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<AccountToken> AccountTokens => Set<AccountToken>();
    public DbSet<MatchHistoryEntry> MatchHistory => Set<MatchHistoryEntry>();
    public DbSet<DailyArenaStats> DailyArenaStats => Set<DailyArenaStats>();
    public DbSet<OperationsEvent> OperationsEvents => Set<OperationsEvent>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<PlayerReport> PlayerReports => Set<PlayerReport>();
    public DbSet<PvpLobbyEntry> PvpLobby => Set<PvpLobbyEntry>();
    public DbSet<HubMessage> HubMessages => Set<HubMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        // This automatically discovers and applies all fluent configurations in this project assembly
        modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
    }
}
