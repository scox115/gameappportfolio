namespace Game.Infrastructure.Messaging;

/// <summary>
/// A SignalR message for players who may be connected to another API replica. The replica that
/// sends it delivers it to its own connections and saves this row; every other replica reads new
/// rows a few times a second and delivers them to its connections. Rows are deleted after a couple
/// of minutes. See docs/adr/0027-scale-out.md.
/// </summary>
public class HubMessage
{
    public const int HubMaxLength = 100;
    public const int MethodMaxLength = 100;
    public const int GroupMaxLength = 100;

    public long Id { get; private set; }

    /// <summary>The hub's class name, such as "ArenaHub".</summary>
    public string Hub { get; private set; } = string.Empty;

    /// <summary>The client method to call, such as "BattleUpdated".</summary>
    public string Method { get; private set; } = string.Empty;

    /// <summary>The user ids to send to, comma-separated, or null for everyone connected to the hub.</summary>
    public string? UserIds { get; private set; }

    /// <summary>The group to send to, such as the spectators of one duel; when set, <see cref="UserIds"/> is null.</summary>
    public string? Group { get; private set; }

    /// <summary>The method's arguments, as a JSON array.</summary>
    public string Arguments { get; private set; } = string.Empty;

    /// <summary>The replica that sent it, which has already delivered it to its own connections.</summary>
    public Guid Origin { get; private set; }

    public DateTime CreatedAt { get; private set; }

    private HubMessage() { }

    public HubMessage(string hub, string method, IReadOnlyList<string>? userIds, string arguments, Guid origin, DateTime createdAt, string? group = null)
    {
        Hub = hub;
        Method = method;
        UserIds = userIds is null ? null : string.Join(',', userIds);
        Group = group;
        Arguments = arguments;
        Origin = origin;
        CreatedAt = createdAt;
    }

    public IReadOnlyList<string>? Recipients => UserIds?.Split(',', StringSplitOptions.RemoveEmptyEntries);
}
