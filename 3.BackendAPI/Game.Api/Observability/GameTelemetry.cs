using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Game.Api.Observability;

/// <summary>
/// The game's own traces and metrics, exported through OpenTelemetry next to the built-in
/// ASP.NET Core, HttpClient and runtime instrumentation.
/// </summary>
public static class GameTelemetry
{
    public const string Name = "Game.Api";

    public static readonly ActivitySource ActivitySource = new(Name);

    private static readonly Meter Meter = new(Name);

    private static readonly Counter<long> BattlesCompleted = Meter.CreateCounter<long>(
        "game.battles.completed", unit: "{battle}", description: "Boss fights and duels that finished.");

    private static readonly Counter<long> TelemetrySent = Meter.CreateCounter<long>(
        "game.telemetry.sent", unit: "{message}", description: "Match events delivered to RabbitMQ.");

    private static readonly Counter<long> TelemetryDropped = Meter.CreateCounter<long>(
        "game.telemetry.dropped", unit: "{message}", description: "Match events dropped because the backlog was full.");

    private static readonly Counter<long> CleanupDeleted = Meter.CreateCounter<long>(
        "game.cleanup.deleted", unit: "{row}", description: "Rows the scheduled data cleanup deleted.");

    /// <param name="kind">"pve" or "pvp".</param>
    /// <param name="outcome">For boss fights "victory" or "defeat"; for duels how it ended, such as "Knockout".</param>
    public static void BattleCompleted(string kind, string outcome, string? difficulty = null) =>
        BattlesCompleted.Add(1,
            new KeyValuePair<string, object?>("game.battle.kind", kind),
            new KeyValuePair<string, object?>("game.battle.outcome", outcome),
            new KeyValuePair<string, object?>("game.boss.difficulty", difficulty));

    public static void TelemetryMessageSent() => TelemetrySent.Add(1);

    public static void TelemetryMessageDropped() => TelemetryDropped.Add(1);

    /// <param name="kind">"refresh_token", "boss_fight" or "duel".</param>
    public static void RowsCleanedUp(string kind, int count) =>
        CleanupDeleted.Add(count, new KeyValuePair<string, object?>("game.cleanup.kind", kind));
}
