namespace Game.Client.Services;

/// <summary>One entry of the admin audit log, as the API returns it.</summary>
public record AuditEntryDto(long id, DateTime at, string action, string actorName, Guid targetId, string targetName, string reason, string? detail);

public static class AdminFormat
{
    /// <summary>A time in the admin's own time zone, such as "14 Oct 2026, 03:30".</summary>
    public static string When(DateTimeOffset at) => at.ToLocalTime().ToString("d MMM yyyy, HH:mm");

    public static string When(DateTime utc) => When(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)));
}
