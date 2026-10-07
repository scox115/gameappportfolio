using Game.Api.Admin;
using Game.Api.Endpoints;
using Game.Api.Models;
using Game.Core.Admin;
using Game.Core.Interfaces;
using Game.Core.Moderation;
using Game.Infrastructure.Data;
using Game.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Game.Api.Moderation;

public enum ReportStatus
{
    /// <summary>The report is in the admin queue (or already was, from this player).</summary>
    Received,
    NotFound,
    Invalid
}

public record ReportResult(ReportStatus Status, string? Message = null);

/// <summary>
/// Players report offensive names and portraits; admins work through the reports and rename the
/// hero, take the portrait down, or dismiss them. Like every admin change, each decision is written
/// to the audit log in the same save. See docs/adr/0024-moderation-and-reports.md.
/// </summary>
public class ModerationService(
    AppDbContext dbContext,
    AdminService admin,
    UserManager<ApplicationUser> userManager,
    IStorageService storage,
    TimeProvider timeProvider,
    ILogger<ModerationService> logger)
{
    /// <summary>Reports listed at once in the admin queue.</summary>
    public const int QueueSize = 50;

    /// <summary>Notes shown per queue line, newest first.</summary>
    public const int NotesShown = 5;

    private DateTime Now => timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Files a report. Reporting the same thing again while the first report waits changes nothing,
    /// so pressing the button twice can't make a name look more reported than it is.
    /// </summary>
    public async Task<ReportResult> ReportAsync(Guid reporterId, Guid targetId, ReportRequest request, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(request.Reason)) return new(ReportStatus.Invalid, "Choose what you're reporting: the name or the portrait.");
        if (reporterId == targetId) return new(ReportStatus.Invalid, "You can't report your own hero.");

        var target = await dbContext.Players.AsNoTracking().FirstOrDefaultAsync(p => p.Id == targetId, cancellationToken);
        if (target is null) return new(ReportStatus.NotFound);
        if (request.Reason == ReportReason.Portrait && target.AvatarUrl is null)
            return new(ReportStatus.Invalid, $"{target.Username} has no portrait to report.");

        var waiting = await dbContext.PlayerReports.AnyAsync(r =>
            r.ReporterId == reporterId && r.TargetId == targetId && r.Reason == request.Reason && r.ResolvedAt == null, cancellationToken);
        if (waiting) return new(ReportStatus.Received);

        try
        {
            dbContext.PlayerReports.Add(new PlayerReport(reporterId, targetId, request.Reason, request.Note, Now));
        }
        catch (ArgumentException ex)
        {
            return new(ReportStatus.Invalid, ex.Message.Split(" (Parameter")[0]);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Player {ReporterId} reported the {Reason} of player {TargetId}.", reporterId, request.Reason, targetId);
        return new(ReportStatus.Received);
    }

    /// <summary>Open reports, one line per hero and reason, most reported first.</summary>
    public async Task<List<ReportQueueItem>> QueueAsync(CancellationToken cancellationToken = default)
    {
        var groups = await dbContext.PlayerReports.AsNoTracking()
            .Where(r => r.ResolvedAt == null)
            .GroupBy(r => new { r.TargetId, r.Reason })
            .Select(g => new { g.Key.TargetId, g.Key.Reason, Reports = g.Count(), First = g.Min(r => r.CreatedAt), Last = g.Max(r => r.CreatedAt) })
            .OrderByDescending(g => g.Reports).ThenBy(g => g.First)
            .Take(QueueSize)
            .ToListAsync(cancellationToken);

        var targetIds = groups.Select(g => g.TargetId).Distinct().ToList();
        var players = await dbContext.Players.AsNoTracking()
            .Where(p => targetIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Username, p.AvatarUrl })
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        var notes = await dbContext.PlayerReports.AsNoTracking()
            .Where(r => r.ResolvedAt == null && targetIds.Contains(r.TargetId) && r.Note != null)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new { r.TargetId, r.Reason, r.Note })
            .ToListAsync(cancellationToken);

        // A hero who deleted their account takes their reports with them; this only skips a race with that.
        return groups.Where(g => players.ContainsKey(g.TargetId)).Select(g =>
        {
            var player = players[g.TargetId];
            return new ReportQueueItem(
                g.TargetId, player.Username, g.Reason == ReportReason.Portrait ? player.AvatarUrl : null, g.Reason,
                g.Reports, g.First, g.Last,
                notes.Where(n => n.TargetId == g.TargetId && n.Reason == g.Reason).Take(NotesShown).Select(n => n.Note!).ToList());
        }).ToList();
    }

    /// <summary>
    /// Gives the hero a new name. The player can still sign in with the old one, so the rename never
    /// locks them out, and the old name can't be registered by anyone else.
    /// </summary>
    public async Task<AdminChange> RenameAsync(AdminActor actor, Guid playerId, RenameRequest request, CancellationToken cancellationToken = default)
    {
        if (!AdminService.TryReason(request.Reason, out var reason, out var problem)) return problem;
        var newName = request.NewName?.Trim() ?? string.Empty;
        if (HeroNames.Problem(newName) is { } nameProblem) return AdminChange.Invalid(nameProblem);

        var (player, user) = await admin.LoadAsync(playerId, cancellationToken);
        if (player is null || user is null) return AdminChange.NotFound;
        if (playerId != actor.Id && await admin.IsAdminAsync(playerId, cancellationToken))
            return AdminChange.Conflict($"{player.Username} is an admin. Take them out of Admin:Usernames first.");
        if (player.Username == newName) return AdminChange.Invalid($"The hero is already called {newName}.");

        var normalized = userManager.NormalizeName(newName);
        if (await NameTakenAsync(normalized, playerId, cancellationToken))
            return AdminChange.Conflict($"The name {newName} is already taken.");

        var oldName = player.Username;
        var closed = await ResolveAsync(playerId, ReportReason.Name, ReportOutcome.ActionTaken, cancellationToken);
        // Recorded before the rename, so the entry names the hero as the admin found them.
        admin.Record(actor, AdminAction.Rename, player, reason, $"{oldName} → {newName}{Closed(closed)}");

        user.RememberPreviousName();
        user.UserName = newName;
        user.NormalizedUserName = normalized;
        player.Rename(newName);

        // Other heroes' match history shows the new name too, so the old one isn't kept on display.
        foreach (var entry in await dbContext.MatchHistory.Where(e => e.OpponentId == playerId).ToListAsync(cancellationToken))
        {
            entry.RenameOpponent(newName);
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return AdminChange.Conflict($"{oldName} changed while you were renaming them. Look again and retry.");
        }
        catch (DbUpdateException)
        {
            // Another hero took the name in the same moment (the unique index on names caught it).
            return AdminChange.Conflict($"The name {newName} is already taken.");
        }

        logger.LogWarning("Admin {Admin} renamed player {PlayerId}.", actor.Name, playerId);
        return AdminService.Done(await admin.DetailAsync(player, user, cancellationToken));
    }

    /// <summary>Takes the hero's portrait down and deletes the image.</summary>
    public async Task<AdminChange> RemovePortraitAsync(AdminActor actor, Guid playerId, RemovePortraitRequest request, CancellationToken cancellationToken = default)
    {
        if (!AdminService.TryReason(request.Reason, out var reason, out var problem)) return problem;

        var (player, user) = await admin.LoadAsync(playerId, cancellationToken);
        if (player is null || user is null) return AdminChange.NotFound;
        if (player.AvatarUrl is not { } url) return AdminChange.Conflict($"{player.Username} has no portrait.");

        player.RemoveAvatar();
        var closed = await ResolveAsync(playerId, ReportReason.Portrait, ReportOutcome.ActionTaken, cancellationToken);
        admin.Record(actor, AdminAction.RemovePortrait, player, reason, closed > 0 ? Closed(closed).TrimStart(';', ' ') : null);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return AdminChange.Conflict($"{player.Username} changed while you were working. Look again and retry.");
        }

        try
        {
            await storage.DeleteFileAsync(url, PlayerEndpoints.AvatarContainer);
        }
        catch (Exception ex)
        {
            // The portrait is no longer shown; a leftover file only costs storage.
            logger.LogWarning("Couldn't delete the removed portrait of player {PlayerId}: {Error}", playerId, ex.Message);
        }

        logger.LogWarning("Admin {Admin} removed the portrait of player {PlayerId}.", actor.Name, playerId);
        return AdminService.Done(await admin.DetailAsync(player, user, cancellationToken));
    }

    /// <summary>Closes the open reports about a hero's name or portrait, leaving it as it is.</summary>
    public async Task<AdminChange> DismissAsync(AdminActor actor, Guid playerId, DismissReportsRequest request, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(request.Kind)) return AdminChange.Invalid("Say which reports to dismiss: Name or Portrait.");
        if (!AdminService.TryReason(request.Reason, out var reason, out var problem)) return problem;

        var (player, user) = await admin.LoadAsync(playerId, cancellationToken);
        if (player is null || user is null) return AdminChange.NotFound;

        var closed = await ResolveAsync(playerId, request.Kind, ReportOutcome.Dismissed, cancellationToken);
        if (closed == 0) return AdminChange.Conflict($"Nobody is waiting on an answer about {player.Username}'s {Describe(request.Kind)}.");

        admin.Record(actor, AdminAction.DismissReports, player, reason, $"{closed} {Describe(request.Kind)} report{(closed == 1 ? "" : "s")} closed.");
        await dbContext.SaveChangesAsync(cancellationToken);
        return AdminService.Done(await admin.DetailAsync(player, user, cancellationToken));
    }

    /// <summary>Whether another hero has, or used to have, this name. Used at sign-up too.</summary>
    public async Task<bool> NameTakenAsync(string normalizedName, Guid? except, CancellationToken cancellationToken = default) =>
        await dbContext.Users.AnyAsync(u =>
            u.Id != except && (u.NormalizedUserName == normalizedName || u.PreviousNormalizedUserName == normalizedName), cancellationToken);

    private async Task<int> ResolveAsync(Guid targetId, ReportReason reason, ReportOutcome outcome, CancellationToken cancellationToken)
    {
        var open = await dbContext.PlayerReports
            .Where(r => r.TargetId == targetId && r.Reason == reason && r.ResolvedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var report in open) report.Resolve(outcome, Now);
        return open.Count;
    }

    private static string Closed(int reports) => reports == 0 ? "" : $"; {reports} report{(reports == 1 ? "" : "s")} closed.";

    private static string Describe(ReportReason reason) => reason == ReportReason.Name ? "name" : "portrait";
}
