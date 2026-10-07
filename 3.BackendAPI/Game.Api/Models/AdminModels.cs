using Game.Core.Admin;
using Game.Core.Battles;
using Game.Core.Moderation;

namespace Game.Api.Models;

/// <param name="Until">When it ends; null means until an admin reinstates the account.</param>
public record SuspensionResponse(DateTimeOffset? Until, string Reason);

/// <summary>A player as the admin search lists them.</summary>
public record AdminPlayerSummary(
    Guid Id, string Username, HeroClass Class, int Level, int Gold, int Rating, bool IsAdmin, SuspensionResponse? Suspension);

/// <summary>One player in full, with what admins have done to the account, newest first.</summary>
public record AdminPlayerDetail(
    Guid Id, string Username, HeroClass Class, int Level, int ExperiencePoints, int Gold, int Rating,
    int PvpWins, int PvpLosses, bool IsAdmin, SuspensionResponse? Suspension, DateTimeOffset? LockedOutUntil,
    List<AuditEntryResponse> History, string? AvatarUrl = null, List<OpenReportCount>? OpenReports = null,
    bool TwoFactorEnabled = false);

/// <summary>How many players are waiting for an answer about one thing on a hero.</summary>
public record OpenReportCount(ReportReason Reason, int Reports);

/// <summary>
/// One line of the report queue: everything players reported about one hero's name or portrait,
/// with the newest notes they left. Reporters aren't named, so admins judge the name, not the people.
/// </summary>
public record ReportQueueItem(
    Guid TargetId, string TargetName, string? AvatarUrl, ReportReason Reason, int Reports,
    DateTime FirstReportedAt, DateTime LastReportedAt, List<string> Notes);

public record AuditEntryResponse(
    long Id, DateTime At, AdminAction Action, string ActorName, Guid TargetId, string TargetName, string Reason, string? Detail)
{
    public static AuditEntryResponse From(AuditLogEntry e) =>
        new(e.Id, e.At, e.Action, e.ActorName, e.TargetId, e.TargetName, e.Reason, e.Detail);
}

/// <param name="Days">How long, from 1 to 3650 days; leave out to suspend until reinstated.</param>
public record SuspendRequest(string Reason, int? Days = null);

public record ReinstateRequest(string Reason);

/// <param name="Change">Gold to add (positive) or take away (negative).</param>
public record GoldCorrectionRequest(int Change, string Reason);

/// <param name="NewName">The hero's new name; it must follow the same rules as at sign-up.</param>
public record RenameRequest(string NewName, string Reason);

public record RemovePortraitRequest(string Reason);

public record TurnOffTwoFactorForPlayerRequest(string Reason);

/// <param name="Kind">Which reports to close: those about the name or those about the portrait.</param>
public record DismissReportsRequest(ReportReason Kind, string Reason);

/// <summary>A player reporting another hero.</summary>
public record ReportRequest(ReportReason Reason, string? Note = null);
