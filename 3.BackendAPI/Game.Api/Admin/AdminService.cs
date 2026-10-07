using System.Globalization;
using Game.Api.Auth;
using Game.Api.Hubs;
using Game.Api.Models;
using Game.Core.Admin;
using Game.Core.Entities;
using Game.Infrastructure.Data;
using Game.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace Game.Api.Admin;

/// <summary>The admin who is acting, from their access token.</summary>
public record AdminActor(Guid Id, string Name);

public enum AdminChangeStatus
{
    Done,
    NotFound,

    /// <summary>The request can't be done as asked, such as a missing reason or too much gold.</summary>
    Invalid,

    /// <summary>The account isn't in a state that allows it, such as reinstating someone who isn't suspended.</summary>
    Conflict
}

public record AdminChange(AdminChangeStatus Status, string? Message = null, AdminPlayerDetail? Player = null)
{
    public static AdminChange NotFound { get; } = new(AdminChangeStatus.NotFound, "No such player.");
    public static AdminChange Invalid(string message) => new(AdminChangeStatus.Invalid, message);
    public static AdminChange Conflict(string message) => new(AdminChangeStatus.Conflict, message);
}

/// <summary>
/// What admins can do: find players, suspend and reinstate them, and correct their gold. Every
/// change is written to the audit log in the same save as the change itself, so one never
/// happens without the other. See docs/adr/0021-admin-roles-and-audit-log.md.
/// </summary>
public class AdminService(
    AppDbContext dbContext,
    RefreshTokenService refreshTokens,
    SessionNotifier notifier,
    TimeProvider timeProvider,
    ILogger<AdminService> logger)
{
    public const int MaxSuspensionDays = 3650;
    public const int MaxResults = 50;

    private DateTimeOffset Now => timeProvider.GetUtcNow();

    /// <summary>Players whose name contains the search text (all players when it is empty), by name.</summary>
    public async Task<List<AdminPlayerSummary>> SearchAsync(string? search, int take, CancellationToken cancellationToken = default)
    {
        var query = dbContext.Players.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim();
            query = query.Where(p => p.Username.Contains(text));
        }

        var players = await query.OrderBy(p => p.Username).Take(Math.Clamp(take, 1, MaxResults)).ToListAsync(cancellationToken);
        var ids = players.Select(p => p.Id).ToList();
        var users = await dbContext.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, cancellationToken);
        var admins = await AdminIdsAsync(ids, cancellationToken);

        return players.Select(p =>
        {
            var suspension = users.TryGetValue(p.Id, out var user) ? SuspensionOf(user) : null;
            return new AdminPlayerSummary(p.Id, p.Username, p.Class, p.Level, p.Gold, p.Rating, admins.Contains(p.Id), suspension);
        }).ToList();
    }

    public async Task<AdminPlayerDetail?> GetAsync(Guid playerId, CancellationToken cancellationToken = default)
    {
        var player = await dbContext.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Id == playerId, cancellationToken);
        var user = await dbContext.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == playerId, cancellationToken);
        return player is null || user is null ? null : await DetailAsync(player, user, cancellationToken);
    }

    /// <summary>The newest audit entries, before <paramref name="before"/> when given (for paging).</summary>
    public async Task<List<AuditEntryResponse>> AuditLogAsync(int take, long? before, CancellationToken cancellationToken = default)
    {
        var query = dbContext.AuditLog.AsNoTracking();
        if (before is { } id) query = query.Where(e => e.Id < id);
        var entries = await query.OrderByDescending(e => e.Id).Take(Math.Clamp(take, 1, MaxResults)).ToListAsync(cancellationToken);
        return entries.Select(AuditEntryResponse.From).ToList();
    }

    /// <summary>Stops the player signing in, for some days or until reinstated, and signs them out now.</summary>
    public async Task<AdminChange> SuspendAsync(AdminActor actor, Guid playerId, SuspendRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Days is { } days && days is < 1 or > MaxSuspensionDays)
            return AdminChange.Invalid($"A suspension lasts from 1 to {MaxSuspensionDays} days, or leave the days out for until reinstated.");
        if (!TryReason(request.Reason, out var reason, out var problem)) return problem;

        var (player, user) = await LoadAsync(playerId, cancellationToken);
        if (player is null || user is null) return AdminChange.NotFound;
        if (playerId == actor.Id) return AdminChange.Invalid("You can't suspend yourself.");
        if (await IsAdminAsync(playerId, cancellationToken))
            return AdminChange.Conflict($"{player.Username} is an admin. Take them out of Admin:Usernames first.");

        var now = Now;
        var until = request.Days is { } length ? now.AddDays(length) : DateTimeOffset.MaxValue;
        user.Suspend(until, reason);
        await refreshTokens.RevokeAllAsync(playerId);

        var detail = request.Days is { } d
            ? $"For {d} day{(d == 1 ? "" : "s")}, until {Describe(until)}."
            : "Until reinstated.";
        Record(actor, AdminAction.Suspend, player, reason, detail);
        await dbContext.SaveChangesAsync(cancellationToken);

        // The tokens are already refused; this tells an open browser straight away.
        await notifier.SuspendedAsync(playerId);
        logger.LogWarning("Admin {Admin} suspended player {PlayerId}. {Detail}", actor.Name, playerId, detail);
        return Done(await DetailAsync(player, user, cancellationToken));
    }

    /// <summary>Lifts a suspension early.</summary>
    public async Task<AdminChange> ReinstateAsync(AdminActor actor, Guid playerId, ReinstateRequest request, CancellationToken cancellationToken = default)
    {
        if (!TryReason(request.Reason, out var reason, out var problem)) return problem;

        var (player, user) = await LoadAsync(playerId, cancellationToken);
        if (player is null || user is null) return AdminChange.NotFound;
        if (!user.IsSuspended(Now)) return AdminChange.Conflict($"{player.Username} isn't suspended.");

        user.Reinstate();
        Record(actor, AdminAction.Reinstate, player, reason);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogWarning("Admin {Admin} reinstated player {PlayerId}.", actor.Name, playerId);
        return Done(await DetailAsync(player, user, cancellationToken));
    }

    /// <summary>Adds or takes away gold, such as to make good a bug or undo an exploit.</summary>
    public async Task<AdminChange> CorrectGoldAsync(AdminActor actor, Guid playerId, GoldCorrectionRequest request, CancellationToken cancellationToken = default)
    {
        if (!TryReason(request.Reason, out var reason, out var problem)) return problem;

        var (player, user) = await LoadAsync(playerId, cancellationToken);
        if (player is null || user is null) return AdminChange.NotFound;

        var before = player.Gold;
        try
        {
            player.CorrectGold(request.Change);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return AdminChange.Invalid(ex.Message.Split(" (Parameter")[0]);
        }

        Record(actor, AdminAction.AdjustGold, player, reason,
            $"{request.Change:+#,0;-#,0} gold ({before:N0} → {player.Gold:N0}).");
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The player spent or earned gold at the same moment; the admin should look again.
            return AdminChange.Conflict($"{player.Username}'s gold changed while you were correcting it. Look again and retry.");
        }

        logger.LogWarning("Admin {Admin} changed the gold of player {PlayerId} by {Change}.", actor.Name, playerId, request.Change);
        return Done(await DetailAsync(player, user, cancellationToken));
    }

    private async Task<(Player? Player, ApplicationUser? User)> LoadAsync(Guid playerId, CancellationToken cancellationToken) =>
        (await dbContext.Players.FindAsync([playerId], cancellationToken),
         await dbContext.Users.FirstOrDefaultAsync(u => u.Id == playerId, cancellationToken));

    private void Record(AdminActor actor, AdminAction action, Player target, string reason, string? detail = null) =>
        dbContext.AuditLog.Add(new AuditLogEntry(
            Now.UtcDateTime, action, actor.Id, actor.Name, target.Id, target.Username, reason, detail));

    private async Task<AdminPlayerDetail> DetailAsync(Player p, ApplicationUser user, CancellationToken cancellationToken)
    {
        var history = await dbContext.AuditLog.AsNoTracking()
            .Where(e => e.TargetId == p.Id)
            .OrderByDescending(e => e.Id)
            .Take(MaxResults)
            .ToListAsync(cancellationToken);

        var lockedOut = user.LockoutEnd is { } end && end > Now ? end : (DateTimeOffset?)null;
        return new AdminPlayerDetail(
            p.Id, p.Username, p.Class, p.Level, p.ExperiencePoints, p.Gold, p.Rating, p.PvpWins, p.PvpLosses,
            await IsAdminAsync(p.Id, cancellationToken), SuspensionOf(user), lockedOut,
            history.Select(AuditEntryResponse.From).ToList());
    }

    private SuspensionResponse? SuspensionOf(ApplicationUser user) =>
        user.IsSuspended(Now)
            ? new SuspensionResponse(user.SuspendedUntil == DateTimeOffset.MaxValue ? null : user.SuspendedUntil, user.SuspensionReason ?? string.Empty)
            : null;

    private async Task<bool> IsAdminAsync(Guid playerId, CancellationToken cancellationToken) =>
        (await AdminIdsAsync([playerId], cancellationToken)).Count > 0;

    private async Task<HashSet<Guid>> AdminIdsAsync(List<Guid> playerIds, CancellationToken cancellationToken)
    {
        var ids = await (
            from link in dbContext.UserRoles
            join role in dbContext.Roles on link.RoleId equals role.Id
            where role.Name == GameRoles.Admin && playerIds.Contains(link.UserId)
            select link.UserId).ToListAsync(cancellationToken);
        return ids.ToHashSet();
    }

    private static bool TryReason(string? given, out string reason, out AdminChange problem)
    {
        try
        {
            reason = AuditLogEntry.RequireReason(given);
            problem = null!;
            return true;
        }
        catch (ArgumentException ex)
        {
            reason = string.Empty;
            problem = AdminChange.Invalid(ex.Message.Split(" (Parameter")[0]);
            return false;
        }
    }

    private static AdminChange Done(AdminPlayerDetail player) => new(AdminChangeStatus.Done, Player: player);

    /// <summary>A time players and admins can read, such as "14 Oct 2026 03:30 UTC".</summary>
    public static string Describe(DateTimeOffset at) =>
        at.UtcDateTime.ToString("d MMM yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture);
}
