namespace Game.Api.ScaleOut;

/// <summary>How SignalR messages reach players connected to other API replicas.</summary>
public enum BackplaneKind
{
    /// <summary>One replica: every player is connected here, so nothing needs passing on.</summary>
    None,

    /// <summary>Messages pass through the HubMessages table in the game's own database. Free, a fraction of a second slower.</summary>
    Sql,

    /// <summary>Redis pub/sub, using ConnectionStrings:Redis. Faster, but needs a Redis server.</summary>
    Redis
}

/// <summary>Settings for running more than one API replica. See docs/adr/0027-scale-out.md.</summary>
public class ScaleOutOptions
{
    public const string SectionName = "ScaleOut";

    public BackplaneKind Backplane { get; set; } = BackplaneKind.None;

    /// <summary>How often a replica with connected players reads new messages from the HubMessages table.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How long messages stay in the HubMessages table.</summary>
    public TimeSpan MessageLifetime { get; set; } = TimeSpan.FromMinutes(2);
}
